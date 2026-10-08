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
    private readonly IUserMessenger? _messenger;
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
        IDiagnosticsService? diagnostics = null,
        IUserMessenger? messenger = null)
    {
        _configStore = configStore;
        _permits = permits;
        _policy = policy;
        _schedule = schedule;
        _enforcement = enforcement;
        _clock = clock;
        _parser = parser ?? new RemoteCommandParser();
        _diagnostics = diagnostics;
        _messenger = messenger;
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
                RemoteCommandType.BrowserPolicyStatus => BuildBrowserPolicyStatus(),
                RemoteCommandType.SetBrowserPolicy =>
                    await HandleSetBrowserPolicyAsync(command, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.Doctor => await RunDoctorAsync(cancellationToken).ConfigureAwait(false),
                RemoteCommandType.DnsTest =>
                    await _enforcement.RunDnsSelfTestAsync(cancellationToken).ConfigureAwait(false),
                RemoteCommandType.DnsRestore =>
                    await _enforcement.RestoreAdapterDnsAsync(cancellationToken).ConfigureAwait(false),
                RemoteCommandType.SendMessage =>
                    await HandleSendMessageAsync(command, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.Ping => BuildPing(),
                RemoteCommandType.Version => BuildVersion(),

                RemoteCommandType.Permit => await HandlePermitAsync(command, source, cancellationToken).ConfigureAwait(false),

                RemoteCommandType.SetSchedule or RemoteCommandType.SetDefaultSchedule =>
                    await HandleSetScheduleAsync(command, cancellationToken).ConfigureAwait(false),
                RemoteCommandType.SetMode =>
                    await HandleSetModeAsync(command, source, cancellationToken).ConfigureAwait(false),
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

    /// <summary>PC 화면에 메시지를 띄운다.</summary>
    private async Task<string> HandleSendMessageAsync(RemoteCommand command, CancellationToken ct)
    {
        if (_messenger is null) return "ERROR\n이 환경에서는 메시지를 보낼 수 없습니다.";

        var shown = await _messenger.SendToPcAsync(command.Text!, ct).ConfigureAwait(false);

        // 아무도 로그인해 있지 않으면 메시지는 사라진다. 보냈다고 하면 안 된다.
        return shown > 0
            ? $"OK\nPC 화면에 띄웠습니다. (창 {shown}개)"
            : "ERROR\n지금 PC 에 로그인한 사용자가 없어 띄우지 못했습니다.\n로그인한 뒤 다시 보내세요.";
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

        // 상태와 "왜 그런지"를 같은 줄에 적는다.
        // 결과만 보여주면 "스케줄이 맞는데 왜 안 막히냐" 는 혼동이 반복된다.
        foreach (var decision in _policy.EvaluateAll())
        {
            var state = ResponseFormatter.BlockedOrAllowed(decision.IsBlocked);
            var why = decision.Reason switch
            {
                AccessReason.AlwaysOpen => "unblock · 항상 열어둠",
                AccessReason.AlwaysBlocked => "block · 항상 막음",
                AccessReason.TemporaryPermit =>
                    $"일시 허용 {ResponseFormatter.FormatRemaining(decision.PermitExpiresUtc!.Value, nowUtc)}분 남음",
                AccessReason.InBlockingSchedule => "auto · 지금은 차단 시간",
                _ => "auto · 지금은 차단 시간 아님"
            };
            builder.AppendLine($"{decision.Target.ToDisplayName(),-12}: {state,-8}({why})");
        }

        builder.AppendLine();
        builder.AppendLine("Schedule:");
        builder.AppendLine(ResponseFormatter.FormatSchedule(config.Schedule));

        // 스케줄을 무시하도록 고정해 둔 대상이 있으면 분명히 알려준다.
        var pinned = BlockTargets.Real
            .Where(t => config.GetTarget(t).Mode != BlockMode.Schedule)
            .ToList();

        if (pinned.Count > 0)
        {
            builder.AppendLine();
            foreach (var target in pinned)
            {
                builder.AppendLine(
                    $"※ {target.ToDisplayName()} 는 {config.GetTarget(target).Mode.ToDisplayName()} 상태라 위 스케줄을 따르지 않습니다.");
            }
            builder.AppendLine($"   스케줄대로 되돌리려면: auto {pinned[0].ToDisplayName().ToLowerInvariant()}");
        }

        builder.AppendLine();
        builder.AppendLine("Temporary Permit:");
        builder.AppendLine(ResponseFormatter.FormatPermits(_permits.GetActive(), nowUtc));

        // DNS 가 실제로 어떻게 동작 중인지(프록시/폴백)를 함께 보여준다.
        builder.AppendLine();
        builder.AppendLine("DNS:");
        builder.AppendLine(_enforcement.DnsStatus.Format());

        // 쇼츠 차단은 브라우저 정책으로만 동작한다.
        // 실제로 적용됐는지 확인할 방법이 여기뿐이므로 상태에 항상 보여준다.
        builder.AppendLine();
        builder.AppendLine("Browser Policy:");
        builder.AppendLine(_enforcement.BrowserPolicyDescription);

        return builder.ToString().TrimEnd();
    }

    /// <summary>대상별 현재 상태. 상태는 auto / block / unblock 셋 중 하나다.</summary>
    private string BuildTargets()
    {
        var config = _configStore.Current;
        var builder = new StringBuilder();

        foreach (var target in BlockTargets.Real)
        {
            var mode = config.GetTarget(target).Mode;
            builder.AppendLine($"{target.ToDisplayName(),-8}: {mode.ToCommandName(),-8}{mode.ToDisplayName()}");
        }

        builder.AppendLine();
        builder.AppendLine("바꾸려면: block / unblock / auto  (예: auto youtube)");

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

        // 이미 항상 열려 있는 대상에 일시 허용을 주는 것은 의미가 없으므로 알려준다.
        if (target != BlockTarget.All && _configStore.Current.GetTarget(target).Mode == BlockMode.Open)
        {
            var name = target.ToDisplayName().ToLowerInvariant();
            return $"ERROR\n{target.ToDisplayName()} 는 unblock 상태라 이미 항상 열려 있습니다.\n" +
                   $"일시 허용이 필요 없습니다.\n\n스케줄대로 돌리려면: auto {name}\n계속 막으려면: block {name}";
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

    /// <summary>
    /// 대상의 상태를 auto / block / unblock 중 하나로 바꾼다.
    ///
    /// 일시 허용은 상태와 무관하게 "지금만 열어둔다" 는 예외다.
    /// 상태를 바꿀 때 그 예외를 그대로 두면 명령이 먹지 않는 것처럼 보이므로 함께 취소한다.
    /// </summary>
    private async Task<string> HandleSetModeAsync(RemoteCommand command, string source, CancellationToken ct)
    {
        var target = command.Target ?? BlockTarget.All;
        var mode = command.Mode!.Value;
        var config = _configStore.Current;

        var applied = target == BlockTarget.All ? BlockTargets.Real : new[] { target };

        var cancelledPermits = 0;
        foreach (var one in applied)
        {
            config.GetTarget(one).Mode = mode;
            cancelledPermits += _permits.Cancel(one, source);
        }

        // "block shorts" 처럼 Shorts 를 직접 지정해 막으라고 했으면, 그것이 곧 동의다.
        // 쇼츠는 브라우저 정책 없이는 전혀 막히지 않으므로 함께 켜 준다.
        // 설정파일을 직접 고치게 만들면 이 프로그램의 전제(PC 를 만지지 않는다)가 깨진다.
        //
        // all 로 묶어 지정한 경우는 켜지 않는다. PC 전체에 적용되는 변경을
        // 포괄 명령의 부수효과로 일으키면 안 된다. 그 경우는 안내만 한다.
        var turnedPolicyOn = false;
        if (target == BlockTarget.Shorts
            && mode != BlockMode.Open
            && config.Shorts.UseBrowserPolicyBlocking
            && !config.BrowserPolicy.Enabled)
        {
            config.BrowserPolicy.Enabled = true;
            turnedPolicyOn = true;
        }

        await SaveAndApplyAsync(config, ct).ConfigureAwait(false);

        var what = mode switch
        {
            BlockMode.Blocked => "지금부터 계속 막습니다. (스케줄 무시)",
            BlockMode.Open => "지금부터 계속 열어둡니다. (스케줄 무시)",
            _ => "스케줄대로 돌아갑니다. 차단 시간대에만 막힙니다."
        };

        var label = string.Join(", ", applied.Select(t => t.ToDisplayName()));
        var note = cancelledPermits > 0 ? "\n진행 중이던 일시 허용도 취소했습니다." : string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("OK");
        builder.AppendLine($"{label} {what}{note}");
        builder.AppendLine();

        // 바꾼 결과가 지금 어떻게 보이는지 바로 확인시켜 준다.
        foreach (var decision in _policy.EvaluateAll())
        {
            builder.AppendLine(
                $"{decision.Target.ToDisplayName(),-8}: {ResponseFormatter.BlockedOrAllowed(decision.IsBlocked),-8}({decision.Mode.ToCommandName()})");
        }

        if (turnedPolicyOn)
        {
            // 레지스트리 정책을 건드리는 변경이므로 무엇이 켜졌는지 반드시 알린다.
            builder.AppendLine();
            builder.AppendLine("쇼츠를 막으려면 브라우저 정책이 필요해서 함께 켰습니다.");
            builder.AppendLine();
            builder.AppendLine(DescribeWhatTurningOnDoes(config));
            builder.AppendLine();
            builder.AppendLine("정책만 다시 끄려면: policy off");
        }

        // Shorts 는 브라우저 정책으로만 막힌다. 기능이 꺼져 있으면 BLOCKED 로 보이지만
        // 실제로는 아무것도 막히지 않는다. 그 간극을 조용히 두면 안 된다.
        var shortsWarning = DescribeShortsGap(config, applied);
        if (shortsWarning is not null)
        {
            builder.AppendLine();
            builder.AppendLine(shortsWarning);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Shorts 를 막으라고 했지만 브라우저 정책이 꺼져 있어 실제로는 적용되지 않는 경우의 안내.
    /// 해당 없으면 null.
    ///
    /// 대상을 직접 지정한 경우(block shorts)는 호출 전에 기능을 켜 주므로 여기 오지 않는다.
    /// 여기에 오는 것은 block all 처럼 전체를 묶어 지정한 경우다.
    /// </summary>
    private static string? DescribeShortsGap(TimeBlockerConfig config, IReadOnlyCollection<BlockTarget> applied)
    {
        if (!applied.Contains(BlockTarget.Shorts)) return null;
        if (config.Shorts.Mode == BlockMode.Open) return null;
        if (config.BrowserPolicy.Enabled && config.Shorts.UseBrowserPolicyBlocking) return null;

        return "※ Shorts 는 아직 실제로 막히지 않습니다.\n" +
               "   쇼츠는 URL 경로를 봐야 해서 브라우저 정책이 필요합니다.\n" +
               "   'policy on' 을 보내면 켜집니다. (또는 'block shorts')";
    }

    // --------------------------------------------------------- 브라우저 정책

    /// <summary>`policy` - 브라우저 정책이 지금 어떤 상태인지.</summary>
    private string BuildBrowserPolicyStatus()
    {
        var config = _configStore.Current;
        var policy = config.BrowserPolicy;
        var builder = new StringBuilder();

        builder.AppendLine($"Browser Policy : {(policy.Enabled ? "ON" : "OFF")}");
        builder.AppendLine(_enforcement.BrowserPolicyDescription);
        builder.AppendLine();

        builder.AppendLine("쇼츠 차단은 이 기능으로만 동작합니다.");
        builder.AppendLine($"Shorts 상태    : {config.Shorts.Mode.ToCommandName()} ({config.Shorts.Mode.ToDisplayName()})");

        if (config.Shorts.BlockedUrlPatterns.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("차단 URL:");
            foreach (var pattern in config.Shorts.BlockedUrlPatterns) builder.AppendLine($"  {pattern}");
        }

        builder.AppendLine();
        builder.AppendLine("함께 막는 우회 경로:");
        builder.AppendLine($"  시크릿 모드  : {OnOff(policy.DisableIncognito)}");
        builder.AppendLine($"  게스트 모드  : {OnOff(policy.DisableGuestMode)}");
        builder.AppendLine($"  브라우저 DoH : {OnOff(policy.DisableDnsOverHttps)}");
        builder.AppendLine($"  확장 설치    : {OnOff(policy.BlockExtensionInstalls)}");

        builder.AppendLine();
        builder.Append(policy.Enabled ? "끄려면: policy off" : "켜려면: policy on");

        return builder.ToString();

        static string OnOff(bool blocked) => blocked ? "차단" : "허용";
    }

    /// <summary>
    /// `policy on` / `policy off`.
    ///
    /// 끄면 바꿔놓은 레지스트리 정책을 원래 값으로 되돌린다. (다음 평가 주기에 수행)
    /// 설정파일을 직접 고치지 않고도 되돌릴 수 있어야 하므로 반드시 원격으로 제공한다.
    /// </summary>
    private async Task<string> HandleSetBrowserPolicyAsync(RemoteCommand command, CancellationToken ct)
    {
        var enable = command.Enable!.Value;
        var config = _configStore.Current;

        if (config.BrowserPolicy.Enabled == enable)
        {
            return $"이미 {(enable ? "켜져" : "꺼져")} 있습니다.\n\n{BuildBrowserPolicyStatus()}";
        }

        config.BrowserPolicy.Enabled = enable;
        await SaveAndApplyAsync(config, ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("OK");

        if (enable)
        {
            builder.AppendLine("브라우저 정책을 켰습니다.");
            builder.AppendLine();
            builder.AppendLine(DescribeWhatTurningOnDoes(config));
        }
        else
        {
            builder.AppendLine("브라우저 정책을 껐습니다.");
            builder.AppendLine("바꿔놓은 레지스트리 정책을 원래 값으로 되돌렸습니다.");
            builder.AppendLine("쇼츠 차단과 시크릿/DoH 차단이 모두 해제됩니다.");
        }

        builder.AppendLine();
        builder.Append(_enforcement.BrowserPolicyDescription);

        return builder.ToString();
    }

    /// <summary>
    /// 기능을 켤 때 함께 적용되는 것을 분명히 알려준다.
    /// PC 전체에 적용되는 변경이므로 조용히 넘어가면 안 된다.
    /// </summary>
    private static string DescribeWhatTurningOnDoes(TimeBlockerConfig config)
    {
        var policy = config.BrowserPolicy;
        var builder = new StringBuilder();

        builder.AppendLine("이 PC 의 브라우저에 아래가 함께 적용됩니다.");
        builder.AppendLine("  - 쇼츠 경로 차단 (일반 YouTube 영상은 그대로)");
        if (policy.DisableIncognito) builder.AppendLine("  - 시크릿 모드 사용 불가");
        if (policy.DisableGuestMode) builder.AppendLine("  - 게스트 모드 사용 불가");
        if (policy.DisableDnsOverHttps) builder.AppendLine("  - 브라우저 DoH 끔 (DNS 차단 우회 방지)");
        if (policy.BlockExtensionInstalls) builder.AppendLine("  - 확장 프로그램 설치 차단");

        builder.AppendLine();
        builder.Append($"대상 브라우저: {string.Join(", ", policy.Browsers)}");

        return builder.ToString();
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
