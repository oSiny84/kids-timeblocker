using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Core;

/// <summary>
/// 정책을 실제 시스템(DNS / 방화벽)에 적용하는 쪽을 명령 처리기에서 호출하기 위한 접점.
/// Shared 는 Windows 구현을 알 필요가 없으므로 인터페이스만 둔다.
/// </summary>
public interface IEnforcementController
{
    /// <summary>지금 즉시 정책을 재계산하고 DNS/방화벽에 반영한다.</summary>
    Task ApplyNowAsync(CancellationToken cancellationToken = default);

    /// <summary>서비스가 시작된 시각(UTC). uptime 계산용.</summary>
    DateTimeOffset StartedAtUtc { get; }

    /// <summary>현재 적용 중인 차단 방식 요약. 예: "DNS Proxy + Firewall"</summary>
    string BlockingModeDescription { get; }

    /// <summary>DNS 차단이 지금 실제로 어떤 상태인지. status 명령에서 사용한다.</summary>
    DnsRuntimeStatus DnsStatus { get; }

    /// <summary>방화벽 차단 대상으로 탐지된 실행파일 목록.</summary>
    IReadOnlyList<string> DetectedExecutables { get; }

    /// <summary>
    /// 브라우저 정책(URL 경로 차단)이 지금 어떤 상태인지. status 명령에서 사용한다.
    /// 쇼츠 차단이 실제로 적용됐는지 확인하는 유일한 수단이므로 상태에 반드시 노출한다.
    /// </summary>
    string BrowserPolicyDescription { get; }

    /// <summary>`dns test` - 지금 즉시 DNS self-test 를 수행하고 결과를 돌려준다.</summary>
    Task<string> RunDnsSelfTestAsync(CancellationToken cancellationToken = default);

    /// <summary>`dns restore` - 어댑터 DNS 를 저장된 원래 설정으로 즉시 되돌린다.</summary>
    Task<string> RestoreAdapterDnsAsync(CancellationToken cancellationToken = default);
}

/// <summary>테스트용 더미 구현.</summary>
public sealed class NullEnforcementController : IEnforcementController
{
    public int ApplyCount { get; private set; }

    public Task ApplyNowAsync(CancellationToken cancellationToken = default)
    {
        ApplyCount++;
        return Task.CompletedTask;
    }

    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string BlockingModeDescription => "None (test)";

    public DnsRuntimeStatus DnsStatus { get; set; } = new()
    {
        Enabled = true,
        Mode = DnsBlockingMode.ProxyWithHostsFallback,
        ProxyState = DnsProxyState.Running,
        HostsFallbackActive = false,
        UpstreamServers = new[] { "1.1.1.1", "8.8.8.8" }
    };

    public IReadOnlyList<string> DetectedExecutables => Array.Empty<string>();

    public string BrowserPolicyDescription { get; set; } = "Disabled";

    /// <summary>테스트에서 돌려줄 self-test 결과 문자열.</summary>
    public string SelfTestResponse { get; set; } = "OK\n\nProxy direct     : OK (test)";

    public int SelfTestCount { get; private set; }

    public int RestoreCount { get; private set; }

    public Task<string> RunDnsSelfTestAsync(CancellationToken cancellationToken = default)
    {
        SelfTestCount++;
        return Task.FromResult(SelfTestResponse);
    }

    public Task<string> RestoreAdapterDnsAsync(CancellationToken cancellationToken = default)
    {
        RestoreCount++;
        return Task.FromResult("OK\n어댑터 0개의 DNS 를 원래 설정으로 되돌렸습니다.");
    }
}
