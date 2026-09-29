namespace TimeBlocker.Shared.Models;

/// <summary>차단 여부가 그렇게 결정된 이유.</summary>
public enum AccessReason
{
    /// <summary>항상 열어두도록 설정됨 (unblock).</summary>
    AlwaysOpen,

    /// <summary>일시 허용이 활성 상태.</summary>
    TemporaryPermit,

    /// <summary>항상 막도록 설정됨 (block). 스케줄과 무관하다.</summary>
    AlwaysBlocked,

    /// <summary>현재 시각이 차단 스케줄 안.</summary>
    InBlockingSchedule,

    /// <summary>차단 시간대가 아님.</summary>
    OutsideSchedule
}

/// <summary>
/// AccessPolicyEngine 의 판정 결과.
/// DNS / 방화벽 / GUI 는 각자 판단하지 않고 항상 이 결과만 사용한다.
/// </summary>
public sealed class AccessDecision
{
    public BlockTarget Target { get; init; }

    public bool IsBlocked { get; init; }

    public AccessReason Reason { get; init; }

    /// <summary>판정 당시 이 대상의 상태(auto / block / unblock).</summary>
    public BlockMode Mode { get; init; } = BlockMode.Schedule;

    /// <summary>일시 허용으로 인해 열린 경우 그 만료 시각(UTC).</summary>
    public DateTimeOffset? PermitExpiresUtc { get; init; }

    /// <summary>현재 적용 중이거나 다음에 적용될 차단 구간 설명. 예: "21:00 ~ 07:00"</summary>
    public string ScheduleDescription { get; init; } = string.Empty;

    public override string ToString() =>
        $"{Target.ToDisplayName()}: {(IsBlocked ? "BLOCKED" : "ALLOWED")} ({Reason})";
}
