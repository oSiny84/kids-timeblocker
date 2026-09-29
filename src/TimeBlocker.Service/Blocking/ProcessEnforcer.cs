using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;
using TimeBlocker.Shared.Notifications;

namespace TimeBlocker.Service.Blocking;

/// <summary>
/// 차단 시간인데 게임이 돌고 있으면 유예 시간을 주고 종료한다.
///
/// 흐름 (대상마다 따로):
///   1. 차단 상태 + 해당 프로세스가 돌고 있음 -> 화면에 경고, 종료 예정 시각 기록
///   2. 1분 남았을 때 한 번 더 경고
///   3. 유예 시간이 지나면 종료
///
/// 언제든 차단이 풀리면(일시 허용 / unblock / 스케줄 종료) 예약은 취소되고
/// 다시 차단될 때 유예 시간이 처음부터 다시 주어진다.
/// 게임을 아이가 스스로 껐을 때도 예약은 취소된다.
/// </summary>
public sealed class ProcessEnforcer
{
    /// <summary>종료 1분 전에 마지막으로 한 번 더 알린다.</summary>
    private static readonly TimeSpan FinalWarning = TimeSpan.FromMinutes(1);

    private readonly IRunningProcessTracker _processes;
    private readonly IUserAlertService _alerts;
    private readonly ISystemClock _clock;
    private readonly ILogger<ProcessEnforcer> _logger;

    /// <summary>대상별 종료 예정 상태.</summary>
    private readonly Dictionary<BlockTarget, PendingTermination> _pending = new();

    public ProcessEnforcer(
        IRunningProcessTracker processes,
        IUserAlertService alerts,
        ISystemClock clock,
        ILogger<ProcessEnforcer> logger)
    {
        _processes = processes;
        _alerts = alerts;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>지금 종료 예정인 대상들. status 표시에 쓴다.</summary>
    public IReadOnlyList<PendingTerminationInfo> Pending
    {
        get
        {
            lock (_pending)
            {
                return _pending
                    .Select(kv => new PendingTerminationInfo(kv.Key, kv.Value.DeadlineUtc))
                    .OrderBy(p => p.DeadlineUtc)
                    .ToList();
            }
        }
    }

    /// <summary>평가 주기마다 호출된다.</summary>
    public void Evaluate(IReadOnlyList<AccessDecision> decisions, TimeBlockerConfig config)
    {
        var grace = TimeSpan.FromMinutes(Math.Max(0, config.Enforcement.TerminationGraceMinutes));
        var nowUtc = _clock.UtcNow;

        foreach (var decision in decisions)
        {
            var settings = config.GetTarget(decision.Target);

            if (!settings.TerminateProcesses || settings.ProcessNames.Count == 0)
            {
                Cancel(decision.Target, "설정에서 꺼져 있음");
                continue;
            }

            if (!decision.IsBlocked)
            {
                Cancel(decision.Target, "차단 상태가 아님");
                continue;
            }

            var running = _processes.Find(settings.ProcessNames);
            if (running.Count == 0)
            {
                Cancel(decision.Target, "실행 중이 아님");
                continue;
            }

            HandleRunning(decision.Target, running, grace, nowUtc);
        }
    }

    private void HandleRunning(
        BlockTarget target,
        IReadOnlyList<RunningProcess> running,
        TimeSpan grace,
        DateTimeOffset nowUtc)
    {
        PendingTermination pending;

        lock (_pending)
        {
            if (!_pending.TryGetValue(target, out var existing))
            {
                pending = new PendingTermination { DeadlineUtc = nowUtc + grace };
                _pending[target] = pending;

                _logger.LogWarning(
                    "{Target} 이(가) 차단 시간에 실행 중입니다. {Minutes}분 뒤 종료 예정입니다. (프로세스 {Count}개)",
                    target.ToDisplayName(), grace.TotalMinutes, running.Count);

                WarnFirst(target, pending.DeadlineUtc, grace);
                return;
            }

            pending = existing;
        }

        var remaining = pending.DeadlineUtc - nowUtc;

        if (remaining > TimeSpan.Zero)
        {
            // 마지막 경고는 딱 한 번만 띄운다.
            if (remaining <= FinalWarning && !pending.FinalWarned)
            {
                pending.FinalWarned = true;
                WarnFinal(target);
            }
            return;
        }

        Terminate(target, running);
    }

    private void Terminate(BlockTarget target, IReadOnlyList<RunningProcess> running)
    {
        var killed = 0;
        foreach (var process in running)
        {
            if (_processes.TryTerminate(process)) killed++;
        }

        _logger.LogWarning(
            "{Target} 프로세스 {Killed}/{Total} 개를 종료했습니다. (차단 시간)",
            target.ToDisplayName(), killed, running.Count);

        _alerts.Alert(
            NotificationKind.Terminated,
            "TimeBlocker",
            $"지금은 {target.ToDisplayName()} 차단 시간이라 종료했습니다.\n\n" +
            "더 하고 싶으면 부모님께 말씀드리세요.");

        // 예약을 지운다. 그래도 계속 다시 켜면 다음 주기에 유예 시간이 새로 주어진다.
        lock (_pending) _pending.Remove(target);
    }

    private void WarnFirst(BlockTarget target, DateTimeOffset deadlineUtc, TimeSpan grace)
    {
        var minutes = (int)Math.Round(grace.TotalMinutes);
        var localDeadline = deadlineUtc.ToLocalTime();

        _alerts.Alert(
            NotificationKind.Warning,
            "TimeBlocker",
            $"지금은 {target.ToDisplayName()} 차단 시간입니다.\n\n" +
            $"{minutes}분 뒤인 {localDeadline:HH:mm} 에 자동으로 종료됩니다.\n" +
            "하던 것을 저장하고 정리해 주세요.");
    }

    private void WarnFinal(BlockTarget target)
    {
        _alerts.Alert(
            NotificationKind.Warning,
            "TimeBlocker",
            $"{target.ToDisplayName()} 이(가) 1분 뒤 종료됩니다.\n\n지금 저장하세요.");
    }

    private void Cancel(BlockTarget target, string reason)
    {
        lock (_pending)
        {
            if (!_pending.Remove(target)) return;
        }

        _logger.LogInformation("{Target} 종료 예약을 취소했습니다: {Reason}", target.ToDisplayName(), reason);
    }

    private sealed class PendingTermination
    {
        public DateTimeOffset DeadlineUtc { get; init; }

        public bool FinalWarned { get; set; }
    }
}

/// <summary>종료 예정 정보. 외부 표시용.</summary>
public readonly record struct PendingTerminationInfo(BlockTarget Target, DateTimeOffset DeadlineUtc);
