using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Blocking;

public interface IFirewallManager
{
    /// <summary>주어진 실행파일들을 아웃바운드 차단한다. 기존 TimeBlocker 규칙은 교체된다.</summary>
    Task<bool> BlockAsync(string ruleName, IReadOnlyCollection<string> executablePaths, CancellationToken ct);

    /// <summary>해당 이름의 TimeBlocker 규칙을 모두 제거한다.</summary>
    Task<bool> ClearAsync(string ruleName, CancellationToken ct);

    /// <summary>해당 이름의 규칙이 존재하는지.</summary>
    Task<bool> ExistsAsync(string ruleName, CancellationToken ct);
}

/// <summary>
/// Windows 방화벽 규칙 관리 (netsh advfirewall).
///
/// 규칙 이름을 TimeBlocker_* 로 통일해서 어떤 규칙이 이 프로그램 것인지 바로 알 수 있게 한다.
/// 차단 해제 시에는 규칙을 남겨두지 않고 삭제한다.
/// (비활성 규칙을 남기면 사용자가 방화벽 UI 에서 손대기 쉽고 상태가 애매해진다)
/// </summary>
public sealed class FirewallManager : IFirewallManager
{
    public const string RobloxRuleName = "TimeBlocker_Roblox_Block";
    public const string YouTubeRuleName = "TimeBlocker_YouTube_Block";

    private readonly ILogger<FirewallManager> _logger;

    public FirewallManager(ILogger<FirewallManager> logger)
    {
        _logger = logger;
    }

    public async Task<bool> BlockAsync(
        string ruleName,
        IReadOnlyCollection<string> executablePaths,
        CancellationToken ct)
    {
        if (executablePaths.Count == 0)
        {
            _logger.LogWarning("방화벽 차단 대상 실행파일을 찾지 못했습니다. 규칙 {Rule} 을 만들지 않습니다.", ruleName);
            return false;
        }

        // 항상 기존 규칙을 지우고 새로 만든다. (경로가 바뀌었을 수 있음 - Roblox 버전 업데이트)
        await DeleteRuleAsync(ruleName, ct).ConfigureAwait(false);

        var anyCreated = false;
        foreach (var path in executablePaths)
        {
            if (!File.Exists(path))
            {
                _logger.LogDebug("실행파일이 없어 건너뜁니다: {Path}", path);
                continue;
            }

            // 아웃바운드 차단만으로 온라인 게임 접속을 막을 수 있다.
            var arguments =
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block " +
                $"program=\"{path}\" enable=yes profile=any";

            var result = await ProcessRunner.RunAsync("netsh", arguments, _logger, cancellationToken: ct)
                .ConfigureAwait(false);

            if (result.Success)
            {
                anyCreated = true;
                _logger.LogInformation("방화벽 차단 규칙 추가: {Rule} -> {Path}", ruleName, path);
            }
            else
            {
                _logger.LogError(
                    "방화벽 규칙 추가 실패 ({Rule} -> {Path}): {Error}",
                    ruleName, path, Summarize(result));
            }
        }

        return anyCreated;
    }

    public async Task<bool> ClearAsync(string ruleName, CancellationToken ct)
    {
        var deleted = await DeleteRuleAsync(ruleName, ct).ConfigureAwait(false);
        if (deleted) _logger.LogInformation("방화벽 차단 규칙 제거: {Rule}", ruleName);
        return deleted;
    }

    public async Task<bool> ExistsAsync(string ruleName, CancellationToken ct)
    {
        var result = await ProcessRunner
            .RunAsync("netsh", $"advfirewall firewall show rule name=\"{ruleName}\"", _logger, cancellationToken: ct)
            .ConfigureAwait(false);

        // 규칙이 없으면 exit code 1 과 "No rules match..." 메시지가 나온다.
        return result.Success;
    }

    private async Task<bool> DeleteRuleAsync(string ruleName, CancellationToken ct)
    {
        var result = await ProcessRunner
            .RunAsync("netsh", $"advfirewall firewall delete rule name=\"{ruleName}\"", _logger, cancellationToken: ct)
            .ConfigureAwait(false);

        // 지울 규칙이 없는 경우도 실패로 나오지만 정상 상황이므로 로그만 DEBUG 로 남긴다.
        if (!result.Success) _logger.LogDebug("제거할 방화벽 규칙이 없습니다: {Rule}", ruleName);
        return result.Success;
    }

    private static string Summarize(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 300 ? text[..300] : text;
    }
}
