using System.Text;
using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Configuration;

namespace TimeBlocker.Service.Blocking;

public interface IHostsFileManager
{
    /// <summary>TimeBlocker 관리 구간을 주어진 도메인 목록으로 교체한다. 변경이 있었으면 true.</summary>
    bool Apply(IReadOnlyCollection<string> domains);

    /// <summary>TimeBlocker 관리 구간을 제거한다. 변경이 있었으면 true.</summary>
    bool Clear();

    /// <summary>현재 관리 구간에 들어 있는 도메인.</summary>
    IReadOnlyList<string> GetManagedDomains();
}

/// <summary>
/// hosts 파일의 TimeBlocker 전용 구간만 관리한다.
///
///   # TIMEBLOCKER BEGIN
///   0.0.0.0 youtube.com
///   # TIMEBLOCKER END
///
/// 마커 바깥의 기존 내용은 절대 건드리지 않는다.
/// 관리자 권한(LocalSystem)이 필요하다.
/// </summary>
public sealed class HostsFileManager : IHostsFileManager
{
    private const string BeginMarker = "# TIMEBLOCKER BEGIN";
    private const string EndMarker = "# TIMEBLOCKER END";
    private const string BlockAddress = "0.0.0.0";
    private const string BlockAddressV6 = "::1";

    private readonly ILogger<HostsFileManager> _logger;
    private readonly string _hostsPath;
    private readonly object _lock = new();

    // 같은 실패가 매 주기(기본 10초)마다 반복되면 로그가 순식간에 커진다.
    // 첫 실패는 바로 남기고, 이후 같은 원인은 일정 간격으로만 요약해서 남긴다.
    private static readonly TimeSpan RepeatedFailureLogInterval = TimeSpan.FromMinutes(5);
    private string? _lastFailureKey;
    private DateTimeOffset _lastFailureLoggedAt = DateTimeOffset.MinValue;
    private int _suppressedFailureCount;

    public HostsFileManager(ILogger<HostsFileManager> logger, string? hostsPath = null)
    {
        _logger = logger;
        _hostsPath = hostsPath ?? AppPaths.HostsFile;
    }

    public bool Apply(IReadOnlyCollection<string> domains)
    {
        // 중복/대소문자 정리. IPv6 차단도 함께 넣기 위해 :: 항목도 추가한다.
        var normalized = domains
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0 && !d.StartsWith('#'))
            .Distinct()
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0) return Clear();

        var block = new StringBuilder();
        block.AppendLine(BeginMarker);
        block.AppendLine("# 이 구간은 TimeBlocker 가 자동으로 관리합니다. 직접 수정하지 마세요.");
        foreach (var domain in normalized)
        {
            block.AppendLine($"{BlockAddress} {domain}");
            block.AppendLine($"{BlockAddressV6} {domain}");
        }
        block.Append(EndMarker);

        return ReplaceManagedSection(block.ToString());
    }

    public bool Clear() => ReplaceManagedSection(null);

    public IReadOnlyList<string> GetManagedDomains()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_hostsPath)) return Array.Empty<string>();

                var lines = File.ReadAllLines(_hostsPath);
                var (start, end) = FindMarkers(lines);
                if (start < 0 || end < 0) return Array.Empty<string>();

                var domains = new List<string>();
                for (var i = start + 1; i < end; i++)
                {
                    var parts = lines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0] == BlockAddress) domains.Add(parts[1]);
                }
                return domains;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "hosts 파일을 읽지 못했습니다.");
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>managedBlock 이 null 이면 구간을 제거한다.</summary>
    private bool ReplaceManagedSection(string? managedBlock)
    {
        lock (_lock)
        {
            try
            {
                var original = File.Exists(_hostsPath) ? File.ReadAllText(_hostsPath) : string.Empty;
                var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

                var (start, end) = FindMarkers(lines);
                if (start >= 0 && end >= 0)
                {
                    lines.RemoveRange(start, end - start + 1);
                    // 구간 제거 후 남은 빈 줄 정리
                    while (start < lines.Count && string.IsNullOrWhiteSpace(lines[start])) lines.RemoveAt(start);
                }

                if (managedBlock is not null)
                {
                    // 파일 끝에 붙인다. 앞에 빈 줄 하나로 구분.
                    while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
                    lines.Add(string.Empty);
                    lines.AddRange(managedBlock.Replace("\r\n", "\n").Split('\n'));
                }

                var updated = string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;

                // 내용이 같으면 쓰지 않는다. (매 주기마다 hosts 를 건드리면 안 된다)
                if (NormalizeForCompare(updated) == NormalizeForCompare(original)) return false;

                WriteHostsFile(updated);
                ClearFailureState();
                _logger.LogInformation(
                    managedBlock is null ? "hosts 차단 구간을 제거했습니다." : "hosts 차단 구간을 갱신했습니다.");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                LogFailure("denied", ex, "hosts 파일을 쓸 권한이 없습니다. 서비스가 관리자 권한으로 실행 중인지 확인하세요.");
                return false;
            }
            catch (Exception ex)
            {
                LogFailure(ex.GetType().Name, ex, "hosts 파일을 갱신하지 못했습니다.");
                return false;
            }
        }
    }

    /// <summary>
    /// 같은 실패가 반복될 때 로그가 폭증하지 않도록 억제한다.
    /// 첫 발생은 즉시 ERROR 로 남기고, 이후 같은 원인은 5분마다 억제 횟수와 함께 한 번만 남긴다.
    /// </summary>
    private void LogFailure(string failureKey, Exception ex, string message)
    {
        var now = DateTimeOffset.UtcNow;

        if (failureKey == _lastFailureKey && now - _lastFailureLoggedAt < RepeatedFailureLogInterval)
        {
            _suppressedFailureCount++;
            _logger.LogDebug("{Message} (반복 {Count}회째, 요약 로그 대기 중)", message, _suppressedFailureCount + 1);
            return;
        }

        if (failureKey == _lastFailureKey && _suppressedFailureCount > 0)
        {
            _logger.LogError(ex, "{Message} (지난 {Minutes}분간 같은 오류 {Count}회 반복)",
                message, (int)RepeatedFailureLogInterval.TotalMinutes, _suppressedFailureCount + 1);
        }
        else
        {
            _logger.LogError(ex, "{Message}", message);
        }

        _lastFailureKey = failureKey;
        _lastFailureLoggedAt = now;
        _suppressedFailureCount = 0;
    }

    private void ClearFailureState()
    {
        if (_lastFailureKey is null) return;

        _logger.LogInformation("hosts 파일 접근이 정상으로 돌아왔습니다.");
        _lastFailureKey = null;
        _suppressedFailureCount = 0;
        _lastFailureLoggedAt = DateTimeOffset.MinValue;
    }

    private void WriteHostsFile(string content)
    {
        // 백신/다른 프로그램이 hosts 를 읽기 모드로 잡고 있을 수 있어 짧게 재시도한다.
        var attributes = File.Exists(_hostsPath) ? File.GetAttributes(_hostsPath) : FileAttributes.Normal;
        var wasReadOnly = attributes.HasFlag(FileAttributes.ReadOnly);

        if (wasReadOnly) File.SetAttributes(_hostsPath, attributes & ~FileAttributes.ReadOnly);

        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    File.WriteAllText(_hostsPath, content, new UTF8Encoding(false));
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(250);
                }
            }
        }
        finally
        {
            if (wasReadOnly && File.Exists(_hostsPath))
            {
                File.SetAttributes(_hostsPath, File.GetAttributes(_hostsPath) | FileAttributes.ReadOnly);
            }
        }
    }

    private static (int start, int end) FindMarkers(IList<string> lines)
    {
        var start = -1;
        var end = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (start < 0 && trimmed.Equals(BeginMarker, StringComparison.OrdinalIgnoreCase)) start = i;
            else if (start >= 0 && trimmed.Equals(EndMarker, StringComparison.OrdinalIgnoreCase))
            {
                end = i;
                break;
            }
        }

        // BEGIN 은 있는데 END 가 없는 손상 상태면 파일 끝까지를 구간으로 본다.
        if (start >= 0 && end < 0) end = lines.Count - 1;
        return (start, end);
    }

    private static string NormalizeForCompare(string text) =>
        text.Replace("\r\n", "\n").TrimEnd();
}
