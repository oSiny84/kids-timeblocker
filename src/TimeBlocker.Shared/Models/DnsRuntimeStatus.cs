using TimeBlocker.Shared.Configuration;

namespace TimeBlocker.Shared.Models;

/// <summary>DNS 차단이 지금 실제로 어떻게 동작하고 있는지.</summary>
public enum DnsProxyState
{
    /// <summary>설정에서 DNS 차단 자체를 껐거나 Hosts 모드라 프록시를 쓰지 않는다.</summary>
    NotUsed,

    /// <summary>프록시가 정상 동작 중.</summary>
    Running,

    /// <summary>프록시를 시작하지 못했거나 치명적 오류로 멈췄다.</summary>
    Failed,

    /// <summary>아직 시작 전.</summary>
    Stopped
}

/// <summary>
/// status 명령에서 보여줄 DNS 런타임 상태.
/// 설정값(Mode)과 실제 동작 상태(프록시가 실제로 떠 있는지, 폴백 중인지)를 함께 담는다.
/// </summary>
public sealed class DnsRuntimeStatus
{
    public bool Enabled { get; init; } = true;

    /// <summary>설정된 모드.</summary>
    public DnsBlockingMode Mode { get; init; }

    public DnsProxyState ProxyState { get; init; } = DnsProxyState.Stopped;

    /// <summary>프록시가 실패해서 hosts 방식으로 돌아간 상태인지.</summary>
    public bool HostsFallbackActive { get; init; }

    public IReadOnlyList<string> UpstreamServers { get; init; } = Array.Empty<string>();

    /// <summary>마지막 실패 이유. 민감정보를 담지 않는다.</summary>
    public string? LastError { get; init; }

    // ---------------------------------------------- 어댑터 DNS 관련

    /// <summary>설정에서 어댑터 DNS 자동 변경을 켰는지.</summary>
    public bool AutoConfigureEnabled { get; init; }

    /// <summary>지금 어댑터 DNS 가 실제로 무엇으로 되어 있는지. 예: "127.0.0.1", "(원래 설정)"</summary>
    public string AdapterDns { get; init; } = "(원래 설정)";

    /// <summary>DNS 를 변경한 어댑터 이름 목록.</summary>
    public IReadOnlyList<string> ConfiguredAdapters { get; init; } = Array.Empty<string>();

    /// <summary>원래 DNS 설정을 디스크에 저장해 두었는지. 복구 가능 여부를 뜻한다.</summary>
    public bool OriginalDnsSaved { get; init; }

    /// <summary>마지막 self-test 결과. null 이면 아직 수행하지 않음.</summary>
    public bool? SelfTestPassed { get; init; }

    public string? SelfTestDetail { get; init; }

    public static DnsRuntimeStatus Disabled(DnsBlockingMode mode) => new()
    {
        Enabled = false,
        Mode = mode,
        ProxyState = DnsProxyState.NotUsed
    };

    /// <summary>
    /// status 응답용 문자열. 요청된 형식을 그대로 따른다.
    ///
    ///   DNS Mode      : ProxyWithHostsFallback
    ///   DNS Proxy     : RUNNING
    ///   Fallback      : NOT ACTIVE
    ///   Upstream DNS  : 1.1.1.1
    /// </summary>
    public string Format()
    {
        var builder = new System.Text.StringBuilder();

        if (!Enabled)
        {
            builder.AppendLine($"{"DNS Mode",-19}: Disabled");
            builder.Append($"{"DNS Proxy",-19}: NOT USED");
            return builder.ToString();
        }

        builder.AppendLine($"{"DNS Mode",-19}: {Mode}");

        var proxyText = ProxyState switch
        {
            DnsProxyState.Running => "RUNNING",
            DnsProxyState.Failed => "FAILED",
            DnsProxyState.NotUsed => "NOT USED",
            _ => "STOPPED"
        };
        builder.AppendLine($"{"DNS Proxy",-19}: {proxyText}");

        var fallbackText = Mode switch
        {
            // hosts 가 원래 주 방식인 모드에서는 폴백이라는 개념이 없다.
            DnsBlockingMode.Hosts => "N/A (hosts is primary)",
            DnsBlockingMode.Proxy => HostsFallbackActive ? "HOSTS ACTIVE" : "DISABLED (proxy only)",
            _ => HostsFallbackActive ? "HOSTS ACTIVE" : "NOT ACTIVE"
        };
        // 어댑터 DNS 는 인터넷이 끊길 수 있는 부분이라 항상 보여준다.
        builder.AppendLine($"{"Adapter DNS",-19}: {AdapterDns}");
        builder.AppendLine($"{"Auto Configure",-19}: {(AutoConfigureEnabled ? "ENABLED" : "DISABLED")}");

        var selfTestText = SelfTestPassed switch
        {
            true => "OK",
            false => $"FAILED{(string.IsNullOrWhiteSpace(SelfTestDetail) ? "" : $" ({SelfTestDetail})")}",
            _ => "NOT RUN"
        };
        builder.AppendLine($"{"DNS Self Test",-19}: {selfTestText}");
        builder.AppendLine($"{"Original DNS Saved",-19}: {(OriginalDnsSaved ? "YES" : "NO")}");
        builder.Append($"{"Fallback",-19}: {fallbackText}");

        if (ConfiguredAdapters.Count > 0)
        {
            builder.AppendLine();
            builder.Append($"{"Adapters",-19}: {string.Join(", ", ConfiguredAdapters)}");
        }

        // 상위 DNS 는 프록시가 실제로 돌 때만 의미가 있다.
        if (ProxyState == DnsProxyState.Running && UpstreamServers.Count > 0)
        {
            builder.AppendLine();
            builder.Append($"{"Upstream DNS",-19}: {string.Join(", ", UpstreamServers)}");
        }

        if (!string.IsNullOrWhiteSpace(LastError))
        {
            builder.AppendLine();
            builder.Append($"{"Last Error",-19}: {LastError}");
        }

        return builder.ToString();
    }
}
