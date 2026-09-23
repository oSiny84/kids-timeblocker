namespace TimeBlocker.Shared.Models;

/// <summary>대상 1개의 현재 상태. GUI / Telegram 응답에 그대로 쓰인다.</summary>
public sealed class TargetStatus
{
    public BlockTarget Target { get; set; }
    public bool Enabled { get; set; }
    public bool IsBlocked { get; set; }
    public AccessReason Reason { get; set; }
    public DateTimeOffset? PermitExpiresUtc { get; set; }
    public string ScheduleDescription { get; set; } = string.Empty;
}

/// <summary>서비스 전체 상태 스냅샷.</summary>
public sealed class ServiceStatus
{
    public string ServiceVersion { get; set; } = "1.0.0";
    public DateTimeOffset NowUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StartedAtUtc { get; set; }
    public List<TargetStatus> Targets { get; set; } = new();

    /// <summary>현재 적용 중인 차단 방식 요약. 예: "Hosts + Firewall"</summary>
    public string BlockingModeDescription { get; set; } = string.Empty;

    public bool DnsProxyRunning { get; set; }
    public bool RemoteProviderRunning { get; set; }
    public string? RemoteProviderStatus { get; set; }
    public List<string> DetectedRobloxExecutables { get; set; } = new();
    public string? LastError { get; set; }

    public TargetStatus? Find(BlockTarget target) => Targets.FirstOrDefault(t => t.Target == target);
}
