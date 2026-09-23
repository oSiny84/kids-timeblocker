using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Blocking.Dns;

public interface INetworkAdapterDnsConfigurator
{
    /// <summary>
    /// 실제 사용 중인 어댑터의 DNS 를 로컬 프록시로 바꾼다.
    /// 변경 <b>전에</b> 원래 설정을 디스크에 저장한다. 저장에 실패하면 아무것도 바꾸지 않는다.
    /// </summary>
    Task<AdapterDnsApplyResult> PointToLocalProxyAsync(bool configureIpv6, CancellationToken ct);

    /// <summary>저장해 둔 원래 설정으로 되돌린다. 성공하면 상태 파일을 지운다.</summary>
    Task<int> RestoreOriginalAsync(CancellationToken ct);

    /// <summary>
    /// 이전 실행이 비정상 종료되어 어댑터 DNS 가 127.0.0.1 로 남아 있는지 확인하고 복구한다.
    /// 서비스 시작 시 가장 먼저 호출한다.
    /// </summary>
    Task<int> RecoverFromUncleanShutdownAsync(CancellationToken ct);

    /// <summary>현재 우리가 변경해 둔 어댑터 이름 목록. (상태 파일 기준)</summary>
    IReadOnlyList<string> ConfiguredAdapterNames { get; }

    /// <summary>원래 설정이 저장되어 있는지.</summary>
    bool HasSavedOriginal { get; }

    /// <summary>실제 어댑터에 적용된 IPv4 DNS 를 읽어 요약한다. 진단용.</summary>
    string DescribeCurrentAdapterDns();
}

public sealed record AdapterDnsApplyResult(bool Success, int ChangedCount, string? Error)
{
    public static AdapterDnsApplyResult Fail(string error) => new(false, 0, error);
}

/// <summary>
/// DNS 프록시 모드에서 네트워크 어댑터의 DNS 를 127.0.0.1 로 바꾼다.
///
/// 이 클래스는 PC 인터넷을 끊어버릴 수 있는 부분이므로 다음 원칙을 지킨다.
///  1. 바꾸기 전에 원래 설정을 반드시 디스크에 저장한다. 저장 실패 시 변경하지 않는다.
///  2. 원래 설정은 레지스트리에서 읽는다. (netsh 출력 파싱은 Windows 표시 언어에 따라 달라져 위험하다)
///  3. 연결되어 있고 게이트웨이가 있는 실제 어댑터만 건드린다. 가상/루프백/끊긴 어댑터는 제외한다.
///  4. 변경 후 실제로 반영됐는지 읽어서 확인한다.
///  5. 언제든 RestoreOriginalAsync 로 되돌릴 수 있고, 되돌리면 상태 파일을 지운다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkAdapterDnsConfigurator : INetworkAdapterDnsConfigurator
{
    private const string LoopbackIpv4 = "127.0.0.1";
    private const string LoopbackIpv6 = "::1";

    private const string Ipv4InterfaceKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string Ipv6InterfaceKey = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    /// <summary>설명에 이 단어가 들어간 어댑터는 실제 네트워크 어댑터로 보지 않는다.</summary>
    private static readonly string[] VirtualAdapterKeywords =
    {
        "virtual", "vmware", "hyper-v", "virtualbox", "vbox", "tap-", "tun",
        "loopback", "wsl", "npcap", "bluetooth", "wan miniport", "docker",
        "pseudo", "teredo", "isatap", "vpn"
    };

    private readonly ILogger<NetworkAdapterDnsConfigurator> _logger;
    private readonly IAdapterDnsStateStore _stateStore;

    public NetworkAdapterDnsConfigurator(
        ILogger<NetworkAdapterDnsConfigurator> logger,
        IAdapterDnsStateStore stateStore)
    {
        _logger = logger;
        _stateStore = stateStore;
    }

    public bool HasSavedOriginal => _stateStore.Exists;

    public IReadOnlyList<string> ConfiguredAdapterNames =>
        _stateStore.Load()?.Adapters.Select(a => a.Name).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    // ------------------------------------------------------------ 적용

    public async Task<AdapterDnsApplyResult> PointToLocalProxyAsync(bool configureIpv6, CancellationToken ct)
    {
        var adapters = GetTargetAdapters().ToList();
        if (adapters.Count == 0)
        {
            return AdapterDnsApplyResult.Fail("변경할 실제 네트워크 어댑터를 찾지 못했습니다.");
        }

        // 1) 원래 설정을 먼저 수집한다.
        var snapshots = new List<AdapterDnsSnapshot>();
        foreach (var adapter in adapters)
        {
            var snapshot = CaptureSnapshot(adapter);
            snapshots.Add(snapshot);
            _logger.LogInformation("어댑터 원래 DNS 설정 확인: {Snapshot}", snapshot);
        }

        // 2) 어댑터를 건드리기 전에 반드시 저장한다. 저장 실패 시 변경을 포기한다.
        var state = new AdapterDnsState
        {
            Adapters = snapshots,
            ProcessId = Environment.ProcessId,
            AppliedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            _stateStore.Save(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "어댑터 DNS 원본 설정을 저장하지 못했습니다. 안전을 위해 어댑터 DNS 를 변경하지 않습니다.");
            return AdapterDnsApplyResult.Fail("원본 DNS 설정 저장 실패");
        }

        // 3) 실제 변경
        var changed = 0;
        foreach (var snapshot in snapshots)
        {
            var ipv4Ok = await SetDnsAsync("ipv4", snapshot.Name, new[] { LoopbackIpv4 }, ct).ConfigureAwait(false);
            snapshot.Ipv4Changed = ipv4Ok;

            if (ipv4Ok) changed++;
            else _logger.LogWarning("어댑터 IPv4 DNS 변경 실패: {Adapter}", snapshot.Name);

            // IPv6 DNS 를 그대로 두면 Windows 가 IPv6 DNS 로 질의해 프록시를 우회할 수 있다.
            if (configureIpv6)
            {
                var ipv6Ok = await SetDnsAsync("ipv6", snapshot.Name, new[] { LoopbackIpv6 }, ct).ConfigureAwait(false);
                snapshot.Ipv6Changed = ipv6Ok;

                if (!ipv6Ok)
                {
                    _logger.LogWarning(
                        "어댑터 IPv6 DNS 변경 실패: {Adapter}. IPv6 DNS 경로로 프록시가 우회될 수 있습니다.",
                        snapshot.Name);
                }
            }
            else if (!snapshot.Ipv6Dhcp && snapshot.Ipv6Servers.Count > 0)
            {
                _logger.LogWarning(
                    "IPv6 DNS 설정({Servers})이 남아 있어 프록시가 우회될 수 있습니다: {Adapter}",
                    string.Join(", ", snapshot.Ipv6Servers), snapshot.Name);
            }
        }

        // 변경 결과를 다시 저장한다. (어떤 어댑터의 무엇을 바꿨는지 정확히 기록)
        try { _stateStore.Save(state); } catch { /* 이미 1차 저장됨 - 무시 */ }

        if (changed == 0)
        {
            return AdapterDnsApplyResult.Fail("어떤 어댑터의 DNS 도 변경하지 못했습니다.");
        }

        // 4) 실제로 반영되었는지 확인한다.
        if (!VerifyLoopbackApplied(snapshots))
        {
            return new AdapterDnsApplyResult(false, changed, "어댑터 DNS 변경이 실제로 반영되지 않았습니다.");
        }

        _logger.LogInformation("어댑터 DNS 를 로컬 프록시로 변경했습니다 ({Count}개): {Names}",
            changed, string.Join(", ", snapshots.Where(s => s.Ipv4Changed).Select(s => s.Name)));

        return new AdapterDnsApplyResult(true, changed, null);
    }

    // ------------------------------------------------------------ 원복

    public async Task<int> RestoreOriginalAsync(CancellationToken ct)
    {
        var state = _stateStore.Load();
        if (state is null || !state.HasAny)
        {
            return 0;
        }

        var restored = 0;
        var failures = new List<string>();

        foreach (var snapshot in state.Adapters)
        {
            var ok = true;

            if (snapshot.Ipv4Changed)
            {
                ok &= await RestoreOneAsync("ipv4", snapshot.Name, snapshot.Ipv4Dhcp, snapshot.Ipv4Servers, ct)
                    .ConfigureAwait(false);
            }

            if (snapshot.Ipv6Changed)
            {
                ok &= await RestoreOneAsync("ipv6", snapshot.Name, snapshot.Ipv6Dhcp, snapshot.Ipv6Servers, ct)
                    .ConfigureAwait(false);
            }

            if (ok)
            {
                restored++;
                _logger.LogInformation("어댑터 DNS 를 원래 설정으로 복구했습니다: {Snapshot}", snapshot);
            }
            else
            {
                failures.Add(snapshot.Name);
                _logger.LogError(
                    "어댑터 DNS 복구에 실패했습니다: {Adapter}. " +
                    "인터넷이 되지 않으면 네트워크 설정에서 DNS 를 자동 획득으로 바꾸세요.", snapshot.Name);
            }
        }

        // 모두 성공했을 때만 상태 파일을 지운다. 일부 실패하면 다음 시작 때 다시 시도한다.
        if (failures.Count == 0)
        {
            _stateStore.Clear();
        }
        else
        {
            _logger.LogWarning(
                "일부 어댑터 복구에 실패해 상태 파일을 유지합니다. 다음 시작 시 다시 시도합니다: {Adapters}",
                string.Join(", ", failures));
        }

        return restored;
    }

    public async Task<int> RecoverFromUncleanShutdownAsync(CancellationToken ct)
    {
        var state = _stateStore.Load();
        if (state is null || !state.HasAny)
        {
            return 0; // 정상 종료였다.
        }

        _logger.LogWarning(
            "이전 실행이 정상적으로 종료되지 않았습니다. 어댑터 DNS 원본 설정이 남아 있어 먼저 복구합니다. " +
            "(저장 시각: {AppliedAt}, PID: {Pid})",
            state.AppliedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), state.ProcessId);

        var restored = await RestoreOriginalAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("비정상 종료 복구 완료: 어댑터 {Count}개의 DNS 를 원래대로 되돌렸습니다.", restored);
        return restored;
    }

    private async Task<bool> RestoreOneAsync(
        string family, string adapterName, bool dhcp, IReadOnlyList<string> servers, CancellationToken ct)
    {
        if (dhcp)
        {
            var arguments = $"interface {family} set dnsservers name=\"{adapterName}\" source=dhcp";
            var result = await ProcessRunner.RunAsync("netsh", arguments, _logger, cancellationToken: ct)
                .ConfigureAwait(false);
            return result.Success;
        }

        if (servers.Count == 0)
        {
            // 원래 아무 DNS 도 없었던 경우. DHCP 로 되돌리는 것이 가장 안전하다.
            var arguments = $"interface {family} set dnsservers name=\"{adapterName}\" source=dhcp";
            var result = await ProcessRunner.RunAsync("netsh", arguments, _logger, cancellationToken: ct)
                .ConfigureAwait(false);
            return result.Success;
        }

        return await SetDnsAsync(family, adapterName, servers, ct).ConfigureAwait(false);
    }

    /// <summary>수동 DNS 서버 목록을 지정한다. 첫 번째는 set, 나머지는 add 로 순서대로 넣는다.</summary>
    private async Task<bool> SetDnsAsync(
        string family, string adapterName, IReadOnlyList<string> servers, CancellationToken ct)
    {
        var first = $"interface {family} set dnsservers name=\"{adapterName}\" " +
                    $"source=static address={servers[0]} validate=no";

        var result = await ProcessRunner.RunAsync("netsh", first, _logger, cancellationToken: ct)
            .ConfigureAwait(false);

        if (!result.Success) return false;

        for (var index = 1; index < servers.Count; index++)
        {
            var more = $"interface {family} add dnsservers name=\"{adapterName}\" " +
                       $"address={servers[index]} index={index + 1} validate=no";

            var addResult = await ProcessRunner.RunAsync("netsh", more, _logger, cancellationToken: ct)
                .ConfigureAwait(false);

            // 보조 DNS 추가 실패는 치명적이지 않다. 기록만 한다.
            if (!addResult.Success)
            {
                _logger.LogWarning("보조 DNS 추가 실패: {Adapter} {Address}", adapterName, servers[index]);
            }
        }

        return true;
    }

    // ------------------------------------------------------ 어댑터 선택/조회

    /// <summary>
    /// 실제로 사용 중인 어댑터만 고른다.
    ///  - 연결됨(Up)
    ///  - 유선(Ethernet) 또는 무선(Wireless80211)
    ///  - 루프백/터널 제외, 가상 어댑터 제외
    ///  - 게이트웨이가 있는 것 우선 (진짜 인터넷에 붙어 있는 어댑터)
    /// </summary>
    private IEnumerable<NetworkInterface> GetTargetAdapters()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogError(ex, "네트워크 어댑터 목록을 가져오지 못했습니다.");
            return Array.Empty<NetworkInterface>();
        }

        var candidates = all.Where(IsRealConnectedAdapter).ToList();

        // 게이트웨이가 있는 어댑터가 하나라도 있으면 그것만 대상으로 한다.
        var withGateway = candidates.Where(HasGateway).ToList();
        var selected = withGateway.Count > 0 ? withGateway : candidates;

        foreach (var skipped in all.Except(selected))
        {
            _logger.LogDebug("어댑터 제외: {Name} ({Type}, {Status})",
                skipped.Name, skipped.NetworkInterfaceType, skipped.OperationalStatus);
        }

        return selected;
    }

    private bool IsRealConnectedAdapter(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up) return false;

        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        // 유선/무선만 대상으로 한다.
        var isEthernet = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;
        var isWireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

        if (!isEthernet && !isWireless) return false;

        var description = adapter.Description.ToLowerInvariant();
        if (VirtualAdapterKeywords.Any(keyword => description.Contains(keyword, StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses
                .Any(g => g.Address is not null && !g.Address.Equals(IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 레지스트리에서 원래 DNS 설정을 읽는다.
    ///
    /// NameServer      : 수동 지정 DNS. 비어 있으면 DHCP 자동 획득.
    /// DhcpNameServer  : DHCP 가 준 DNS. (참고용, 복구에는 쓰지 않는다)
    /// </summary>
    private AdapterDnsSnapshot CaptureSnapshot(NetworkInterface adapter)
    {
        var snapshot = new AdapterDnsSnapshot
        {
            Name = adapter.Name,
            Id = adapter.Id,
            Description = adapter.Description
        };

        var (ipv4Dhcp, ipv4Servers) = ReadRegistryDns(Ipv4InterfaceKey, adapter.Id);
        snapshot.Ipv4Dhcp = ipv4Dhcp;
        snapshot.Ipv4Servers = ipv4Servers;

        var (ipv6Dhcp, ipv6Servers) = ReadRegistryDns(Ipv6InterfaceKey, adapter.Id);
        snapshot.Ipv6Dhcp = ipv6Dhcp;
        snapshot.Ipv6Servers = ipv6Servers;

        return snapshot;
    }

    private (bool dhcp, List<string> servers) ReadRegistryDns(string basePath, string interfaceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{basePath}\{interfaceId}");
            var nameServer = key?.GetValue("NameServer") as string;

            if (string.IsNullOrWhiteSpace(nameServer))
            {
                return (true, new List<string>()); // 수동 설정 없음 = DHCP
            }

            var servers = nameServer
                .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            return servers.Count == 0 ? (true, new List<string>()) : (false, servers);
        }
        catch (Exception ex)
        {
            // 읽지 못하면 DHCP 로 가정한다. 복구 시 자동 획득으로 되돌아가므로 인터넷은 살아난다.
            _logger.LogWarning(ex,
                "레지스트리에서 DNS 설정을 읽지 못했습니다. DHCP 로 간주합니다: {Interface}", interfaceId);
            return (true, new List<string>());
        }
    }

    /// <summary>변경 후 실제로 어댑터가 루프백 DNS 를 쓰고 있는지 확인한다.</summary>
    private bool VerifyLoopbackApplied(IReadOnlyList<AdapterDnsSnapshot> snapshots)
    {
        try
        {
            var changedNames = snapshots.Where(s => s.Ipv4Changed).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (changedNames.Count == 0) return false;

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!changedNames.Contains(adapter.Name)) continue;

                var dnsAddresses = adapter.GetIPProperties().DnsAddresses;
                if (dnsAddresses.Any(IPAddress.IsLoopback)) return true;
            }

            _logger.LogError("어댑터 DNS 를 바꿨지만 시스템에 반영되지 않았습니다.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "어댑터 DNS 반영 여부를 확인하지 못했습니다.");
            return true; // 확인 실패를 변경 실패로 보지는 않는다.
        }
    }

    public string DescribeCurrentAdapterDns()
    {
        try
        {
            var entries = new List<string>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(IsRealConnectedAdapter))
            {
                var servers = adapter.GetIPProperties().DnsAddresses
                    .Select(a => a.ToString())
                    .ToList();

                entries.Add(servers.Count == 0
                    ? $"{adapter.Name}=(없음)"
                    : $"{adapter.Name}={string.Join("/", servers)}");
            }

            return entries.Count == 0 ? "(어댑터 없음)" : string.Join(", ", entries);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "어댑터 DNS 조회 실패");
            return "(조회 실패)";
        }
    }
}
