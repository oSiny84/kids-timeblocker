using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Core;

public interface IAccessPolicyEngine
{
    AccessDecision Evaluate(BlockTarget target);

    /// <summary>모든 실제 대상(YouTube / Roblox)에 대한 판정.</summary>
    IReadOnlyList<AccessDecision> EvaluateAll();
}

/// <summary>
/// "지금 이 대상이 차단인가?" 를 결정하는 단 하나의 로직.
/// DNS / 방화벽 / GUI / Telegram 은 각자 판단하지 않고 반드시 여기 결과만 쓴다.
///
/// 판정 우선순위:
///   1. Mode=Open   (unblock)            -> ALLOW (AlwaysOpen)
///   2. 일시 허용이 살아 있음            -> ALLOW (TemporaryPermit)
///   3. Mode=Blocked (block)             -> BLOCK (AlwaysBlocked)
///   4. 현재 시각이 차단 스케줄 안       -> BLOCK (InBlockingSchedule)
///   5. 그 외                            -> ALLOW (OutsideSchedule)
///
/// 일시 허용을 Blocked 보다 위에 두는 이유:
/// block 으로 잠가둔 상태에서도 "30분만 열어줘" 가 되어야 하기 때문이다.
/// 반대로 block 명령 자체는 그 대상의 일시 허용을 취소하므로 즉시 막힌다.
/// </summary>
public sealed class AccessPolicyEngine : IAccessPolicyEngine
{
    private readonly Func<TimeBlockerConfig> _configProvider;
    private readonly IScheduleManager _scheduleManager;
    private readonly ITemporaryPermitManager _permitManager;
    private readonly ISystemClock _clock;

    public AccessPolicyEngine(
        Func<TimeBlockerConfig> configProvider,
        IScheduleManager scheduleManager,
        ITemporaryPermitManager permitManager,
        ISystemClock clock)
    {
        _configProvider = configProvider;
        _scheduleManager = scheduleManager;
        _permitManager = permitManager;
        _clock = clock;
    }

    public AccessDecision Evaluate(BlockTarget target)
    {
        if (target == BlockTarget.All)
        {
            throw new ArgumentException("All 은 판정 대상이 아닙니다. YouTube / Roblox 만 평가합니다.", nameof(target));
        }

        var config = _configProvider();
        var localNow = _clock.LocalNow;
        var scheduleText = _scheduleManager.Describe(config.Schedule, localNow);

        // 1. 항상 열어두도록 설정된 대상은 스케줄을 보지 않는다.
        var targetSettings = config.GetTarget(target);
        if (targetSettings.Mode == BlockMode.Open)
        {
            return new AccessDecision
            {
                Target = target,
                IsBlocked = false,
                Reason = AccessReason.AlwaysOpen,
                Mode = targetSettings.Mode,
                ScheduleDescription = scheduleText
            };
        }

        // 2. 일시 허용이 살아 있으면 허용 (스케줄보다 우선)
        var permit = _permitManager.GetEffective(target);
        if (permit is not null)
        {
            return new AccessDecision
            {
                Target = target,
                IsBlocked = false,
                Reason = AccessReason.TemporaryPermit,
                PermitExpiresUtc = permit.ExpireTimeUtc,
                Mode = targetSettings.Mode,
                ScheduleDescription = scheduleText
            };
        }

        // 3. 항상 막도록 설정돼 있으면 스케줄과 무관하게 차단
        if (targetSettings.Mode == BlockMode.Blocked)
        {
            return new AccessDecision
            {
                Target = target,
                IsBlocked = true,
                Reason = AccessReason.AlwaysBlocked,
                Mode = targetSettings.Mode,
                ScheduleDescription = scheduleText
            };
        }

        // 4. 차단 시간대면 차단
        if (_scheduleManager.IsWithinBlockingWindow(config.Schedule, localNow))
        {
            return new AccessDecision
            {
                Target = target,
                IsBlocked = true,
                Reason = AccessReason.InBlockingSchedule,
                Mode = targetSettings.Mode,
                ScheduleDescription = scheduleText
            };
        }

        // 5. 그 외에는 허용
        return new AccessDecision
        {
            Target = target,
            IsBlocked = false,
            Reason = AccessReason.OutsideSchedule,
            Mode = targetSettings.Mode,
            ScheduleDescription = scheduleText
        };
    }

    public IReadOnlyList<AccessDecision> EvaluateAll() =>
        BlockTargets.Real.Select(Evaluate).ToList();
}
