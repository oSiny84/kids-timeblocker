using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Remote;

public interface IRemoteCommandHandler
{
    /// <summary>파싱된 명령을 실행하고 사용자에게 보낼 응답 문자열을 만든다.</summary>
    Task<string> ExecuteAsync(RemoteCommand command, string source, CancellationToken cancellationToken = default);

    /// <summary>문자열을 파싱해서 바로 실행한다. (Telegram / CLI 공통 진입점)</summary>
    Task<string> ExecuteTextAsync(string? text, string source, CancellationToken cancellationToken = default);
}

/// <summary>
/// 명령 실행기. 파서가 만든 RemoteCommand 를 실제 동작으로 옮긴다.
///
/// 처리 흐름(설정 변경 명령의 경우):
///   1. 메모리 설정 변경
///   2. 설정파일 안전 저장 (atomic)
///   3. Policy 즉시 재계산 + DNS/Firewall 반영
///   4. 응답 문자열 생성
///   5. 감사 로그 기록
///
/// 잘못된 입력에서 예외를 던지지 않고 항상 사용법 문자열을 돌려준다.
/// </summary>
public sealed class RemoteCommandHandler : IRemoteCommandHandler
{
    private readonly IConfigurationStore _configStore;
    private readonly ITemporaryPermitManager _permits;
    private readonly IAccessPolicyEngine _policy;
    private readonly IScheduleManager _schedule;
    private readonly IEnforcementController _enforcement;
    private readonly ISystemClock _clock;
    private readonly IRemoteCommandParser _parser;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly ILogger<RemoteCommandHandler> _logger;

    public RemoteCommandHandler(
        IConfigurationStore configStore,
        ITemporaryPermitManager permits,
        IAccessPolicyEngine policy,
        IScheduleManager schedule,
        IEnforcementController enforcement,
        ISystemClock clock,
        IRemoteCommandParser? parser = null,
        ILogger<RemoteCommandHandler>? logger = null,
        IDiagnosticsService? diagnostics = null)
    {
        _configStore = configStore;
        _permits = permits;
        _policy = policy;
        _schedule = schedule;
        _enforcement = enforcement;
        _clock = clock;
        _parser = parser ?? new RemoteCommandParser();
        _diagnostics = diagnostics;
        _logger = logger ?? NullLogger<RemoteCommandHandler>.Instance;
    }

    public async Task<string> ExecuteTextAsync(string? text, string source, CancellationToken cancellationToken = default)
    {
        var command = _parser.Parse(text);
        return await ExecuteAsync(command, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExecuteAsync(
        RemoteCommand command,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = command.Type switch
            {
                RemoteCommandType.Unknown => command.Error ?? "ERROR\nUnknown command.",
                RemoteCommandType.Help => ResponseFormatter.HelpText,
                RemoteCommandType.Status => BuildStatus(),
                RemoteCommandType.Targets => BuildTargets(),
                RemoteCommandType.ShowSchedule => BuildSchedule(),
                RemoteCommandType.ShowDomains => BuildDomains(command),
                RemoteCommandType.ShowMaxPermit => $"Max Permit : {_configStore.Current.TemporaryPermit.MaxMinutes} minutes",
                RemoteCommandType.AdminList => BuildAdminList(),
                RemoteCommandType.DnsStatus => _enforcement.DnsStatus.Format(),
                RemoteCommandType.Doctor => await RunDoctorAsync(cancellationToken).ConfigureAwait(false),
                RemoteCommandType.DnsTest =>
                    await _enforcement.RunDnsSelfTestAsync(cancellationToken).ConfigureAwait(false),
                RemoteCommandType.DnsRestore =>
                    await _enforcement.RestoreAdapterDnsAsync(cancellationToken).ConfigureAwait(false),
                RemoteCommandType.Ping => BuildPing(),
                RemoteCommandType.Version => BuildVersion(),

                RemoteCommandType.Permit => await HandlePermitAsync(command, source, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.Lock => await HandleLockAsync(command, source, cancellationToken).ConfigureAwait(false),

                RemoteCommandType.SetSchedule or RemoteCommandType.SetDefaultSchedule =>
                    await HandleSetScheduleAsync(command, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.EnableTarget or RemoteCommandType.DisableTarget =>
                    await HandleEnableDisableAsync(command, source, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.AddDomain or RemoteCommandType.RemoveDomain =>
                    await HandleDomainAsync(command, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.SetMaxPermit =>
                    await HandleSetMaxPermitAsync(command, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.Reload => await HandleReloadAsync(cancellationToken).ConfigureAwait(false),

                _ => "ERROR\nUnsupported command."
            };

            WriteAuditLog(command, source, success: !response.StartsWith("ERROR", StringComparison.Ordinal));
            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 어떤 명령도 서비스를 죽이면 안 된다.
            _logger.LogError(ex, "명령 처리 중 오류: {Command}", command.Type);
            WriteAuditLog(command, source, success: false);
            return $"ERROR\nCommand failed: {ex.Message}";
        }
    }

    /// <summary>doctor 점검. 진단 서비스가 주입되지 않은 환경(테스트 등)에서는 안내만 돌려준다.</summary>
    private async Task<string> RunDoctorAsync(CancellationToken ct)
    {
        if (_diagnostics is null) return "ERROR\n이 환경에서는 doctor 를 사용할 수 없습니다.";

        var report = await _diagnostics.RunAsync(ct).ConfigureAwait(false);
        return report.Format();
    }

    // ----------------------------------------------------------------- 조회

    private string BuildStatus()
    {
        var config = _configStore.Current;
        var nowUtc = _clock.UtcNow;
        var builder = new StringBuilder();

        builder.AppendLine("TimeBlocker STATUS");
        builder.AppendLine();
        builder.AppendLine("PC          : ONLINE");
        builder.AppendLine("Service     : RUNNING");
        builder.AppendLine();
        builder.AppendLine("Current Time:");
        builder.AppendLine(_clock.LocalNow.ToString("yyyy-MM-dd HH:mm"));
        builder.AppendLine();

        foreach (var decision in _policy.EvaluateAll())
        {
            // "DISABLED" 는 "유튜브가 꺼졌다(=차단됐다)" 로 잘못 읽히기 쉽다.
            // 실제 의미는 "이 대상의 차단 기능이 꺼져 있다" 이므로 그대로 적는다.
            var state = decision.Reason == AccessReason.TargetDisabled
                ? "OFF (차단 안 함)"
                : ResponseFormatter.BlockedOrAllowed(decision.IsBlocked);
            builder.AppendLine($"{decision.Target.ToDisplayName(),-12}: {state}");
        }

        builder.AppendLine();
        builder.AppendLine("Schedule:");
        builder.AppendLine(ResponseFormatter.FormatSchedule(config.Schedule));
        // 차단이 꺼진 대상이 있으면 그것부터 알려준다.
        // (스케줄이 맞는데 왜 안 막히냐는 혼동이 가장 흔하다)
        var offTargets = BlockTargets.Real
            .Where(t => !config.GetTarget(t).Enabled)
            .Select(t => t.ToDisplayName())
            .ToList();

        if (offTargets.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"※ {string.Join(", ", offTargets)} 는 차단이 꺼져 있어 스케줄과 무관하게 열립니다.");
            builder.AppendLine($"   켜려면: block {offTargets[0].ToLowerInvariant()}");
        }

        builder.AppendLine();
        builder.AppendLine("Temporary Permit:");
        builder.AppendLine(ResponseFormatter.FormatPermits(_permits.GetActive(), nowUtc));

        // DNS 가 실제로 어떻게 동작 중인지(프록시/폴백)를 함께 보여준다.
        builder.AppendLine();
        builder.AppendLine("DNS:");
        builder.AppendLine(_enforcement.DnsStatus.Format());

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 대상별 차단 사용 여부.
    ///
    /// "ENABLED / DISABLED" 는 "유튜브를 켠다/끈다" 로 오해하기 쉬워서
    /// "차단 ON / 차단 OFF" 로 적는다. 무엇이 켜지고 꺼지는지 분명하게 보여준다.
    /// </summary>
    private string BuildTargets()
    {
        var config = _configStore.Current;
        var builder = new StringBuilder();

        foreach (var target in BlockTargets.Real)
        {
            var on = config.GetTarget(target).Enabled;
            builder.AppendLine($"{target.ToDisplayName(),-8}: 차단 {(on ? "ON " : "OFF")}  ({(on ? "스케줄대로 차단" : "차단하지 않음")})");
        }

        var offTargets = BlockTargets.Real
            .Where(t => !config.GetTarget(t).Enabled)
            .Select(t => t.ToDisplayName())
            .ToList();

        if (offTargets.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("※ 차단 OFF 인 대상은 스케줄이 맞아도 차단되지 않습니다.");
            builder.AppendLine($"   켜려면: block {offTargets[0].ToLowerInvariant()}");
        }

        return builder.ToString().TrimEnd();
    }

    private string BuildSchedule() => ResponseFormatter.FormatScheduleDetailed(_configStore.Current.Schedule);

    private string BuildDomains(RemoteCommand command)
    {
        var target = command.Target ?? BlockTarget.YouTube;
        var domains = _configStore.Current.GetTarget(target).Domains;

        if (domains.Count == 0) return $"{target.ToDisplayName()} : (no domains)";

        var builder = new StringBuilder();
        builder.AppendLine($"{target.ToDisplayName()} domains ({domains.Count})");
        foreach (var domain in domains.OrderBy(d => d, StringComparer.Ordinal))
        {
            builder.AppendLine(domain);
        }
        return builder.ToString().TrimEnd();
    }

    private string BuildAdminList()
    {
        var ids = _configStore.Current.Telegram.AllowedUserIds;
        if (ids.Count == 0) return "No admin user id configured.";

        var builder = new StringBuilder();
        builder.AppendLine($"Admin user ids ({ids.Count})");
        foreach (var id in ids) builder.AppendLine(id.ToString());
        builder.AppendLine();
        builder.AppendLine("Admin ids can only be changed locally on the PC.");
        return builder.ToString().TrimEnd();
    }

    private string BuildPing()
    {
        var uptime = _clock.UtcNow - _enforcement.StartedAtUtc;
        return $"""
                PONG

                PC      : ONLINE
                Service : RUNNING
                Uptime  : {ResponseFormatter.FormatUptime(uptime)}
                """;
    }

    private string BuildVersion()
    {
        var version = typeof(RemoteCommandHandler).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return $"""
                TimeBlocker v{version}
                {RuntimeInformation.FrameworkDescription}
                {RuntimeInformation.OSDescription}
                Blocking : {_enforcement.BlockingModeDescription}
                """;
    }

    // ------------------------------------------------------- 일시 허용 / 차단

    private async Task<string> HandlePermitAsync(RemoteCommand command, string source, CancellationToken ct)
    {
        var target = command.Target ?? BlockTarget.All;

        // 꺼져 있는 대상에 허용을 주는 것은 의미가 없으므로 알려준다.
        if (target != BlockTarget.All && !_configStore.Current.GetTarget(target).Enabled)
        {
            return $"ERROR\n{target.ToDisplayName()} 는 차단이 꺼져 있어 이미 열려 있습니다.\n" +
                   $"일시 허용이 필요 없습니다.\n\n차단을 켜려면: block {target.ToDisplayName().ToLowerInvariant()}";
        }

        var result = _permits.Grant(target, command.Minutes, source);
        if (!result.Success) return $"ERROR\n{result.Message}";

        // 허용을 주면 즉시 DNS/방화벽 차단을 풀어야 한다.
        await _enforcement.ApplyNowAsync(ct).ConfigureAwait(false);

        var permit = result.Permit!;
        return $"""
                OK
                {target.ToDisplayName()} allowed for {command.Minutes} minutes.

                Start  : {permit.StartTimeUtc.ToLocalTime():HH:mm}
                Expire : {permit.ExpireTimeUtc.ToLocalTime():HH:mm}
                """;
    }

    private async Task<string> HandleLockAsync(RemoteCommand command, string source, CancellationToken ct)
    {
        string header;

        if (command.Target is null)
        {
            _permits.CancelAll(source);
            header = "All temporary permits cancelled.";
        }
        else
        {
            var target = command.Target.Value;
            var removed = _permits.Cancel(target, source);
            header = removed > 0
                ? $"{target.ToDisplayName()} temporary permit cancelled."
                : $"{target.ToDisplayName()} had no active permit.";
        }

        // 취소 후 즉시 스케줄 기준으로 재적용한다.
        await _enforcement.ApplyNowAsync(ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("OK");
        builder.AppendLine(header);
        builder.AppendLine();
        foreach (var decision in _policy.EvaluateAll())
        {
            var state = decision.Reason == AccessReason.TargetDisabled
                ? "DISABLED"
                : ResponseFormatter.BlockedOrAllowed(decision.IsBlocked);
            builder.AppendLine($"{decision.Target.ToDisplayName(),-8}: {state}");
        }
        return builder.ToString().TrimEnd();
    }

    // ------------------------------------------------------------- 설정 변경

    private async Task<string> HandleSetScheduleAsync(RemoteCommand command, CancellationToken ct)
    {
        var config = _configStore.Current;

        foreach (var day in command.Days)
        {
            var rule = config.Schedule.GetDay(day);
            if (command.TurnOff)
            {
                rule.Enabled = false;
            }
            else
            {
                rule.Enabled = true;
                rule.Start = TimeUtil.ToHhMm(command.Start!.Value);
                rule.End = TimeUtil.ToHhMm(command.End!.Value);
            }
        }

        await SaveAndApplyAsync(config, ct).ConfigureAwait(false);

        if (command.Type == RemoteCommandType.SetDefaultSchedule)
        {
            return $"""
                    OK
                    Default blocking schedule updated.

                    {TimeUtil.ToHhMm(command.Start!.Value)} ~ {TimeUtil.ToHhMm(command.End!.Value)}
                    """;
        }

        return "OK\nSchedule updated.\n\n" + ResponseFormatter.FormatScheduleDetailed(config.Schedule);
    }

    private async Task<string> HandleEnableDisableAsync(RemoteCommand command, string source, CancellationToken ct)
    {
        var target = command.Target!.Value;
        var enable = command.Type == RemoteCommandType.EnableTarget;
        var config = _configStore.Current;

        // "block all" / "unblock all" 은 실제 대상 전부에 적용한다.
        var applied = target == BlockTarget.All ? BlockTargets.Real : new[] { target };
        foreach (var one in applied)
        {
            config.GetTarget(one).Enabled = enable;
        }

        // 일시 허용은 스케줄을 덮어쓰는 예외다.
        // "막아라" 라고 했는데 예외가 살아 있으면 명령이 먹지 않는 것처럼 보이므로 같이 치운다.
        // 취소해도 즉시 잠기는 게 아니라, 스케줄 판단으로 돌아갈 뿐이다.
        var cancelledPermits = 0;
        if (enable)
        {
            foreach (var one in applied)
            {
                cancelledPermits += _permits.Cancel(one, source);
            }
        }

        await SaveAndApplyAsync(config, ct).ConfigureAwait(false);

        var what = enable
            ? "차단을 켰습니다. 이제 스케줄에 따라 차단됩니다."
            : "차단을 껐습니다. 스케줄과 무관하게 차단되지 않습니다.";

        var label = target == BlockTarget.All
            ? string.Join(", ", applied.Select(t => t.ToDisplayName()))
            : target.ToDisplayName();

        var note = cancelledPermits > 0 ? "\n진행 중이던 일시 허용도 취소했습니다." : string.Empty;

        return $"OK\n{label} {what}{note}\n\n" + BuildTargets();
    }

    private async Task<string> HandleDomainAsync(RemoteCommand command, CancellationToken ct)
    {
        var target = command.Target!.Value;
        var domain = command.Domain!;
        var config = _configStore.Current;
        var domains = config.GetTarget(target).Domains;

        if (command.Type == RemoteCommandType.AddDomain)
        {
            if (domains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            {
                return $"ERROR\nAlready exists: {domain}";
            }
            domains.Add(domain);
        }
        else
        {
            var removed = domains.RemoveAll(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return $"ERROR\nNot found: {domain}";
        }

        await SaveAndApplyAsync(config, ct).ConfigureAwait(false);

        var verb = command.Type == RemoteCommandType.AddDomain ? "added" : "removed";
        return $"OK\nDomain {verb}: {domain}\n\n{target.ToDisplayName()} domains: {domains.Count}";
    }

    private async Task<string> HandleSetMaxPermitAsync(RemoteCommand command, CancellationToken ct)
    {
        var config = _configStore.Current;
        config.TemporaryPermit.MaxMinutes = command.Value;

        await SaveAndApplyAsync(config, ct).ConfigureAwait(false);

        return $"OK\nMax Permit : {command.Value} minutes";
    }

    private async Task<string> HandleReloadAsync(CancellationToken ct)
    {
        _configStore.Reload();
        await _enforcement.ApplyNowAsync(ct).ConfigureAwait(false);
        return "OK\nConfiguration reloaded and policy re-applied.";
    }

    /// <summary>설정 저장 -> 정책 재계산 -> DNS/방화벽 반영 순서를 한 곳에서 처리한다.</summary>
    private async Task SaveAndApplyAsync(TimeBlockerConfig config, CancellationToken ct)
    {
        _configStore.Save(config);
        await _enforcement.ApplyNowAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- 감사 로그

    /// <summary>
    /// 모든 원격 명령을 기록한다. 토큰 등 민감정보는 명령 문자열에 포함되지 않는다.
    /// (Bot Token 은 로컬 설정에서만 다루며 명령으로 받지 않는다)
    /// </summary>
    private void WriteAuditLog(RemoteCommand command, string source, bool success)
    {
        if (command.Type == RemoteCommandType.Unknown)
        {
            _logger.LogInformation(
                "REMOTE COMMAND | {Source} | Command={Raw} | Result=INVALID", source, Sanitize(command.RawText));
            return;
        }

        var level = command.IsConfigChanging
                    || command.Type is RemoteCommandType.Permit
                        or RemoteCommandType.Lock
                        or RemoteCommandType.DnsRestore
                        or RemoteCommandType.Reload
            ? LogLevel.Information
            : LogLevel.Debug;

        _logger.Log(
            level,
            "REMOTE COMMAND | {Source} | Command={Raw} | Result={Result}",
            source,
            Sanitize(command.RawText),
            success ? "SUCCESS" : "FAILED");
    }

    /// <summary>로그 인젝션을 막기 위해 줄바꿈을 제거하고 길이를 제한한다.</summary>
    private static string Sanitize(string text)
    {
        var cleaned = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }
}
