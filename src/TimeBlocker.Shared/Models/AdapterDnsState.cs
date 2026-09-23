namespace TimeBlocker.Shared.Models;

/// <summary>
/// 어댑터 하나의 원래 DNS 설정 스냅샷.
///
/// 이 값은 어댑터 DNS 를 127.0.0.1 로 바꾸기 <b>전에</b> 반드시 디스크에 저장된다.
/// 서비스가 강제 종료되거나 PC 가 갑자기 꺼져도 다음 시작 때 이 파일로 원래 설정을 복구한다.
/// </summary>
public sealed class AdapterDnsSnapshot
{
    /// <summary>netsh 에서 사용하는 인터페이스 별칭. 예: "이더넷", "Wi-Fi"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>레지스트리 조회용 인터페이스 GUID. 예: "{1A2B...}"</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>어댑터 설명. 진단용. 예: "Intel(R) Wi-Fi 6 AX201"</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>IPv4 DNS 를 DHCP 에서 자동으로 받고 있었는지.</summary>
    public bool Ipv4Dhcp { get; set; } = true;

    /// <summary>IPv4 수동 DNS 서버 목록. Ipv4Dhcp 가 false 일 때만 의미가 있다.</summary>
    public List<string> Ipv4Servers { get; set; } = new();

    /// <summary>IPv6 DNS 를 DHCP 에서 자동으로 받고 있었는지.</summary>
    public bool Ipv6Dhcp { get; set; } = true;

    /// <summary>IPv6 수동 DNS 서버 목록.</summary>
    public List<string> Ipv6Servers { get; set; } = new();

    /// <summary>이 어댑터의 IPv4 DNS 를 우리가 바꿨는지.</summary>
    public bool Ipv4Changed { get; set; }

    /// <summary>이 어댑터의 IPv6 DNS 를 우리가 바꿨는지.</summary>
    public bool Ipv6Changed { get; set; }

    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() =>
        $"{Name} (IPv4: {(Ipv4Dhcp ? "DHCP" : string.Join(",", Ipv4Servers))}, " +
        $"IPv6: {(Ipv6Dhcp ? "DHCP" : string.Join(",", Ipv6Servers))})";
}

/// <summary>
/// 어댑터 DNS 변경 상태 전체. state/adapter-dns.json 으로 저장된다.
///
/// 이 파일이 존재한다 = "어댑터 DNS 를 우리가 바꿔놓은 상태일 수 있다" 는 뜻이다.
/// 정상적으로 원복하면 파일을 지운다.
/// 따라서 서비스 시작 시 이 파일이 남아 있으면 이전 실행이 비정상 종료된 것으로 본다.
/// </summary>
public sealed class AdapterDnsState
{
    public int Version { get; set; } = 1;

    /// <summary>DNS 를 변경한 어댑터 목록과 각각의 원래 설정.</summary>
    public List<AdapterDnsSnapshot> Adapters { get; set; } = new();

    /// <summary>변경을 적용한 프로세스 ID. 진단용.</summary>
    public int ProcessId { get; set; }

    public DateTimeOffset AppliedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool HasAny => Adapters.Count > 0;
}

/// <summary>DNS self-test 결과.</summary>
public sealed class DnsSelfTestResult
{
    public bool Success { get; init; }

    /// <summary>허용 도메인이 정상 응답했는지.</summary>
    public bool AllowedDomainResolved { get; init; }

    /// <summary>차단 도메인이 실제로 차단되었는지.</summary>
    public bool BlockedDomainBlocked { get; init; }

    /// <summary>상위 DNS 에 닿지 못한 경우(인터넷 끊김). 이 경우는 프록시 고장이 아니다.</summary>
    public bool UpstreamUnreachable { get; init; }

    public string Detail { get; init; } = string.Empty;

    public static DnsSelfTestResult Fail(string detail) => new() { Success = false, Detail = detail };
}
