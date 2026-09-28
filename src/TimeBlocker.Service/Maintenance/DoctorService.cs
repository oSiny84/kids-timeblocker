using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Service.Blocking.Dns;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Maintenance;

/// <summary>
/// doctor 명령 구현.
///
/// 설계 원칙: 모든 점검은 <b>바깥에서 관찰 가능한 방식</b>으로 한다.
/// 서비스 내부 상태를 참조하지 않으므로, 서비스가 죽어 있을 때도 그대로 동작한다.
/// (문제가 생겼을 때가 바로 doctor 가 필요한 때이므로 이 성질이 중요하다)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DoctorService : IDiagnosticsService
{
    private const string ServiceName = "TimeBlocker";
    private const int DnsQueryTimeoutMs = 4000;

    private readonly IConfigurationStore _configStore;
    private readonly IHostsFileManager _hosts;
    private readonly IFirewallManager _firewall;
    private readonly INetworkAdapterDnsConfigurator _adapters;
    private readonly RobloxLocator _locator;
    private readonly ILogger _logger;

    public DoctorService(
        IConfigurationStore configStore,
        IHostsFileManager hosts,
        IFirewallManager firewall,
        INetworkAdapterDnsConfigurator adapters,
        RobloxLocator locator,
        ILogger logger)
    {
        _configStore = configStore;
        _hosts = hosts;
        _firewall = firewall;
        _adapters = adapters;
        _locator = locator;
        _logger = logger;
    }

    public async Task<DoctorReport> RunAsync(CancellationToken ct = default)
    {
        var report = new DoctorReport();
        var config = _configStore.Current;

        // --- 권한 / 서비스 ---
        report.Add(CheckAdministrator());
        var (installed, running) = CheckService(report);

        // --- DNS 프록시 ---
        var port = config.Dns.ProxyPort;
        var ipv4Alive = await ProbeProxyAsync(IPAddress.Loopback, port, config, ct).ConfigureAwait(false);
        var ipv6Alive = await ProbeProxyAsync(IPAddress.IPv6Loopback, port, config, ct).ConfigureAwait(false);

        report.Add(CheckPortAvailability(port, ipv4Alive));
        report.Add(CheckProxyListener("DNS Proxy IPv4", $"127.0.0.1:{port}", ipv4Alive, config, running));
        report.Add(CheckProxyListener("DNS Proxy IPv6", $"[::1]:{port}", ipv6Alive, config, running));

        // --- 어댑터 ---
        report.Add(CheckActiveAdapter(out var adapterDnsServers));
        report.Add(CheckAdapterDns(adapterDnsServers, config));
        report.Add(CheckOriginalDnsBackup(config));
        var routedThroughProxy = IsRoutedThroughProxy(adapterDnsServers, config);
        report.Add(CheckAdapterPointsToProxy(adapterDnsServers, config, ipv4Alive));

        // --- 실제 이름 해석 ---
        report.Add(await CheckNormalResolutionAsync(config, ct).ConfigureAwait(false));
        report.Add(await CheckBlockedResolutionAsync(config, ipv4Alive, routedThroughProxy, ct).ConfigureAwait(false));

        // --- 차단 대상 ---
        report.Add(CheckBlockTargets(config));

        // --- hosts / 방화벽 ---
        report.Add(CheckHostsFallback(ipv4Alive, config));
        report.Add(CheckHostsRegionSanity());
        report.Add(await CheckFirewallAsync(config, ct).ConfigureAwait(false));
        report.Add(CheckRoblox(config));

        // --- Telegram ---
        report.Add(CheckTelegramConfigured(config));
        report.Add(CheckTelegramAdminIds(config));
        report.Add(await CheckTelegramApiAsync(config, ct).ConfigureAwait(false));

        // --- 파일 / 권한 ---
        report.Add(CheckConfigValidation());
        report.Add(CheckStateReadWrite());
        report.Add(CheckDataAcl());
        report.Add(CheckLogDirectory());

        return report;
    }

    // ==================================================== 권한 / 서비스

    private static DoctorCheck CheckAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var isAdmin = identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            return isAdmin
                ? DoctorCheck.Pass("Administrator", identity.IsSystem ? "SYSTEM" : identity.Name)
                : DoctorCheck.Warn("Administrator", "아님",
                    "관리자 권한 터미널에서 다시 실행하면 더 많은 항목을 점검할 수 있습니다.");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn("Administrator", ex.GetType().Name);
        }
    }

    private (bool installed, bool running) CheckService(DoctorReport report)
    {
        try
        {
            using var controller = ServiceController.GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

            if (controller is null)
            {
                report.Add(DoctorCheck.Fail("Service installed", "설치되지 않음",
                    "관리자 권한으로 scripts\\install-service.ps1 을 실행하세요."));
                report.Add(DoctorCheck.Fail("Service running", "설치되지 않음"));
                return (false, false);
            }

            report.Add(DoctorCheck.Pass("Service installed", ServiceName));

            var running = controller.Status == ServiceControllerStatus.Running;
            report.Add(running
                ? DoctorCheck.Pass("Service running", controller.Status.ToString())
                : DoctorCheck.Fail("Service running", controller.Status.ToString(),
                    "관리자 권한으로 실행: Start-Service TimeBlocker"));

            return (true, running);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "서비스 상태 조회 실패");
            report.Add(DoctorCheck.Warn("Service installed", $"조회 실패 ({ex.GetType().Name})"));
            report.Add(DoctorCheck.Warn("Service running", "조회 실패"));
            return (false, false);
        }
    }

    // ==================================================== DNS 프록시

    /// <summary>해당 주소의 프록시가 살아 있는지 실제 DNS 질의로 확인한다.</summary>
    private async Task<bool> ProbeProxyAsync(
        IPAddress address, int port, TimeBlockerConfig config, CancellationToken ct)
    {
        try
        {
            var answer = await QueryAsync(config.Dns.SelfTestDomain, address, port, ct).ConfigureAwait(false);
            return answer is not null;
        }
        catch
        {
            return false;
        }
    }

    private DoctorCheck CheckPortAvailability(int port, bool ipv4Alive)
    {
        // 우리 프록시가 이미 응답하고 있으면 포트는 정상적으로 우리가 쓰고 있는 것이다.
        if (ipv4Alive) return DoctorCheck.Pass($"Port {port} (UDP)", "TimeBlocker 사용 중");

        try
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            return DoctorCheck.Pass($"Port {port} (UDP)", "사용 가능");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return DoctorCheck.Fail($"Port {port} (UDP)", "다른 프로그램이 사용 중",
                $"netstat -ano -p UDP | findstr :{port} 로 확인하고 해당 프로그램을 끄거나, " +
                "설정에서 Dns.ProxyPort 를 바꾸세요.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return DoctorCheck.Fail($"Port {port} (UDP)", "권한 없음",
                "1024 미만 포트는 관리자 권한이 필요합니다.");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn($"Port {port} (UDP)", ex.GetType().Name);
        }
    }

    private static DoctorCheck CheckProxyListener(
        string name, string endpoint, bool alive, TimeBlockerConfig config, bool serviceRunning)
    {
        if (config.Dns.Mode == DnsBlockingMode.Hosts)
        {
            return DoctorCheck.Pass(name, "사용 안 함 (Hosts 모드)");
        }

        if (alive) return DoctorCheck.Pass(name, endpoint);

        // 서비스가 아예 안 돌고 있으면 리스너가 없는 것이 당연하다.
        // 이때 IPv6 리스너 오류를 찾아보라고 안내하면 엉뚱한 곳을 보게 된다.
        if (!serviceRunning)
        {
            return DoctorCheck.Fail(name, "응답 없음 (서비스 중지됨)",
                "Start-Service TimeBlocker 로 서비스를 시작한 뒤 다시 점검하세요.");
        }

        // IPv6 는 없어도 동작한다. 다만 우회 가능성이 있으므로 WARN.
        if (name.Contains("IPv6", StringComparison.Ordinal))
        {
            return DoctorCheck.Warn(name, "응답 없음",
                "IPv6 DNS 경로로 차단이 우회될 수 있습니다. 로그에서 IPv6 리스너 오류를 확인하세요.");
        }

        return DoctorCheck.Fail(name, "응답 없음",
            "dns status 로 폴백 여부를 확인하세요. 53 포트 충돌이 가장 흔한 원인입니다.");
    }

    // ==================================================== 어댑터

    private DoctorCheck CheckActiveAdapter(out List<string> dnsServers)
    {
        dnsServers = new List<string>();

        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up)
                .Where(a => a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(a => a.GetIPProperties().GatewayAddresses.Any(g => g.Address is not null))
                .ToList();

            if (active.Count == 0)
            {
                return DoctorCheck.Fail("Active adapter", "없음",
                    "네트워크에 연결되어 있는지 확인하세요.");
            }

            foreach (var adapter in active)
            {
                dnsServers.AddRange(adapter.GetIPProperties().DnsAddresses.Select(a => a.ToString()));
            }

            return DoctorCheck.Pass("Active adapter", string.Join(", ", active.Select(a => a.Name)));
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn("Active adapter", ex.GetType().Name);
        }
    }

    private static DoctorCheck CheckAdapterDns(List<string> dnsServers, TimeBlockerConfig config)
    {
        if (dnsServers.Count == 0)
        {
            return DoctorCheck.Warn("Adapter DNS", "설정된 DNS 없음",
                "네트워크 연결 상태를 확인하세요.");
        }

        return DoctorCheck.Pass("Adapter DNS", string.Join(", ", dnsServers.Distinct()));
    }

    private DoctorCheck CheckOriginalDnsBackup(TimeBlockerConfig config)
    {
        if (!config.Dns.AutoConfigureAdapters)
        {
            return DoctorCheck.Pass("Original DNS backup", "해당 없음 (자동 변경 꺼짐)");
        }

        return _adapters.HasSavedOriginal
            ? DoctorCheck.Pass("Original DNS backup", string.Join(", ", _adapters.ConfiguredAdapterNames))
            : DoctorCheck.Pass("Original DNS backup", "없음 (어댑터 미변경 상태)");
    }

    /// <summary>
    /// 시스템 이름 해석이 실제로 우리 프록시를 거치는 상태인지.
    /// Hosts 모드는 hosts 파일이 시스템 해석에 직접 반영되므로 함께 true 로 본다.
    /// </summary>
    private static bool IsRoutedThroughProxy(List<string> dnsServers, TimeBlockerConfig config)
    {
        if (config.Dns.Mode == DnsBlockingMode.Hosts) return true;

        return dnsServers.Any(s => IPAddress.TryParse(s, out var address) && IPAddress.IsLoopback(address));
    }

    private static DoctorCheck CheckAdapterPointsToProxy(
        List<string> dnsServers, TimeBlockerConfig config, bool proxyAlive)
    {
        const string name = "Adapter -> proxy";

        if (!config.Dns.AutoConfigureAdapters || config.Dns.Mode == DnsBlockingMode.Hosts)
        {
            return DoctorCheck.Pass(name, "해당 없음");
        }

        var pointsToLoopback = dnsServers.Any(s =>
            IPAddress.TryParse(s, out var address) && IPAddress.IsLoopback(address));

        if (pointsToLoopback && proxyAlive) return DoctorCheck.Pass(name, "127.0.0.1 연결됨");

        if (pointsToLoopback && !proxyAlive)
        {
            // 가장 위험한 상태. 인터넷이 끊겨 있을 수 있다.
            return DoctorCheck.Fail(name, "DNS 가 127.0.0.1 인데 프록시가 응답하지 않음",
                "즉시 실행: TimeBlocker.Service.exe dns-restore");
        }

        return DoctorCheck.Warn(name, "어댑터가 프록시를 가리키지 않음",
            "차단이 적용되지 않을 수 있습니다. dns status 로 폴백 여부를 확인하세요.");
    }

    // ==================================================== 이름 해석

    private async Task<DoctorCheck> CheckNormalResolutionAsync(TimeBlockerConfig config, CancellationToken ct)
    {
        const string name = "Normal DNS resolution";
        var domain = config.Dns.SelfTestDomain;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DnsQueryTimeoutMs);

            var addresses = await System.Net.Dns.GetHostAddressesAsync(domain, cts.Token).ConfigureAwait(false);
            var real = addresses.Where(a => !IPAddress.IsLoopback(a)).ToList();

            return real.Count > 0
                ? DoctorCheck.Pass(name, $"{domain} -> {real[0]}")
                : DoctorCheck.Fail(name, $"{domain} 해석 실패",
                    "인터넷 연결을 확인하세요. 계속 안 되면 dns-restore 로 DNS 를 되돌리세요.");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return DoctorCheck.Fail(name, $"{domain} 해석 실패",
                "인터넷이 끊겼거나 DNS 가 잘못되었습니다. TimeBlocker.Service.exe dns-restore 를 실행하세요.");
        }
    }

    private async Task<DoctorCheck> CheckBlockedResolutionAsync(
        TimeBlockerConfig config, bool proxyAlive, bool routedThroughProxy, CancellationToken ct)
    {
        const string name = "Blocked domain";

        var domain = config.YouTube.Domains.FirstOrDefault(d => d.Contains("googlevideo", StringComparison.Ordinal))
                     ?? config.YouTube.Domains.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(domain))
        {
            return DoctorCheck.Warn(name, "차단 도메인이 설정되어 있지 않음",
                "domain add youtube <도메인> 으로 추가하세요.");
        }

        // 대상 자체가 꺼져 있으면 해석되는 것이 당연하다.
        // 이 경우 "차단 시간대가 아님" 같은 엉뚱한 안내를 하면 원인을 못 찾는다.
        if (!config.YouTube.Enabled)
        {
            return DoctorCheck.Warn(name, $"{domain} - YouTube 차단이 OFF 라 확인 불가",
                "텔레그램에서 실행: enable youtube");
        }

        // 시스템 해석이 우리 경로를 거치지 않는 상태라면, 시스템 확인자로 물어봐야 의미가 없다.
        // 이 경우 프록시에 직접 물어서 "프록시 자체는 제대로 막고 있는가"를 확인한다.
        if (!routedThroughProxy)
        {
            if (!proxyAlive) return DoctorCheck.Warn(name, "확인 불가 (프록시 미동작)");

            var direct = await QueryAsync(domain, IPAddress.Loopback, config.Dns.ProxyPort, ct).ConfigureAwait(false);
            var blockedByProxy = direct is { Rcode: 3, AnswerCount: 0, AuthorityCount: 0 };

            return blockedByProxy
                ? DoctorCheck.Warn(name, $"{domain} 은 프록시에서 차단되지만 시스템 DNS 는 프록시를 거치지 않음",
                    "어댑터 DNS 가 127.0.0.1 이 아닙니다. dns status 로 확인하세요.")
                : DoctorCheck.Warn(name, $"{domain} 차단되지 않음 (프록시 직접 질의)",
                    "지금이 차단 시간대가 아니거나 일시 허용 중일 수 있습니다. status 로 확인하세요.");
        }

        // 정상 경로: 시스템 확인자로 물어본다. 이것이 실제 사용자 경험과 같다.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DnsQueryTimeoutMs);

            var addresses = await System.Net.Dns.GetHostAddressesAsync(domain, cts.Token).ConfigureAwait(false);
            var resolved = addresses.Any(a => !IPAddress.IsLoopback(a));

            if (!resolved) return DoctorCheck.Pass(name, $"{domain} blocked");

            return DoctorCheck.Warn(name, $"{domain} 해석됨",
                "지금이 차단 시간대가 아니거나 일시 허용 중일 수 있습니다. status 로 확인하세요.");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return DoctorCheck.Pass(name, $"{domain} blocked");
        }
    }

    /// <summary>
    /// 차단 대상이 켜져 있는지 확인한다.
    ///
    /// 이 항목이 꺼져 있으면 스케줄이 맞아도 아무것도 차단되지 않는다.
    /// 실제로 "설치했는데 유튜브가 그대로 열린다" 의 원인이 대부분 여기다.
    /// </summary>
    private static DoctorCheck CheckBlockTargets(TimeBlockerConfig config)
    {
        const string name = "Block targets";

        var enabled = BlockTargets.Real
            .Where(t => config.GetTarget(t).Enabled)
            .Select(t => t.ToDisplayName())
            .ToList();

        var disabled = BlockTargets.Real
            .Where(t => !config.GetTarget(t).Enabled)
            .Select(t => t.ToDisplayName())
            .ToList();

        if (disabled.Count == 0)
        {
            return DoctorCheck.Pass(name, string.Join(", ", enabled.Select(e => $"{e} 차단 ON")));
        }

        var commands = string.Join(", ", disabled.Select(d => $"enable {d.ToLowerInvariant()}"));

        if (enabled.Count == 0)
        {
            return DoctorCheck.Fail(name, "모든 대상이 꺼져 있어 아무것도 차단되지 않습니다",
                $"텔레그램에서 실행: {commands}");
        }

        return DoctorCheck.Warn(name,
            $"{string.Join(", ", disabled)} 차단 OFF (스케줄과 무관하게 열림)",
            $"텔레그램에서 실행: {commands}");
    }

    // ==================================================== hosts / 방화벽

    private DoctorCheck CheckHostsFallback(bool proxyAlive, TimeBlockerConfig config)
    {
        const string name = "Hosts fallback";

        try
        {
            var managed = _hosts.GetManagedDomains();

            if (config.Dns.Mode == DnsBlockingMode.Hosts)
            {
                return DoctorCheck.Pass(name, $"Hosts 모드 ({managed.Count}개 도메인)");
            }

            if (proxyAlive && managed.Count == 0) return DoctorCheck.Pass(name, "INACTIVE (프록시 사용 중)");

            if (proxyAlive && managed.Count > 0)
            {
                return DoctorCheck.Warn(name, $"프록시와 hosts 가 동시에 활성 ({managed.Count}개)",
                    "reload 를 실행하면 정리됩니다.");
            }

            return managed.Count > 0
                ? DoctorCheck.Warn(name, $"ACTIVE ({managed.Count}개 도메인)",
                    "프록시가 동작하지 않아 hosts 로 차단 중입니다. 하위 도메인 차단이 제한됩니다.")
                : DoctorCheck.Warn(name, "프록시도 hosts 도 동작하지 않음",
                    "차단이 적용되지 않고 있습니다. dns status 로 원인을 확인하세요.");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn(name, ex.GetType().Name);
        }
    }

    private DoctorCheck CheckHostsRegionSanity()
    {
        const string name = "Hosts region";

        try
        {
            var path = AppPaths.HostsFile;
            if (!File.Exists(path)) return DoctorCheck.Warn(name, "hosts 파일 없음");

            var lines = File.ReadAllLines(path);
            var begins = lines.Count(l => l.Trim().StartsWith("# TIMEBLOCKER BEGIN", StringComparison.OrdinalIgnoreCase));
            var ends = lines.Count(l => l.Trim().StartsWith("# TIMEBLOCKER END", StringComparison.OrdinalIgnoreCase));

            if (begins == 0 && ends == 0) return DoctorCheck.Pass(name, "관리 구간 없음");
            if (begins == 1 && ends == 1) return DoctorCheck.Pass(name, "정상");

            return DoctorCheck.Fail(name, $"마커 불일치 (BEGIN {begins}, END {ends})",
                "TimeBlocker.Service.exe cleanup 을 실행하면 관리 구간이 정리됩니다.");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn(name, ex.GetType().Name);
        }
    }

    private async Task<DoctorCheck> CheckFirewallAsync(TimeBlockerConfig config, CancellationToken ct)
    {
        const string name = "Firewall rules";

        if (!config.Roblox.UseFirewallBlocking && !config.YouTube.UseFirewallBlocking)
        {
            return DoctorCheck.Pass(name, "사용 안 함");
        }

        try
        {
            var exists = await _firewall.ExistsAsync(FirewallManager.RobloxRuleName, ct).ConfigureAwait(false);
            return exists
                ? DoctorCheck.Pass(name, FirewallManager.RobloxRuleName)
                : DoctorCheck.Pass(name, "규칙 없음 (현재 차단 시간대가 아님)");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn(name, ex.GetType().Name,
                "관리자 권한이 필요합니다.");
        }
    }

    private DoctorCheck CheckRoblox(TimeBlockerConfig config)
    {
        const string name = "Roblox executable";

        try
        {
            var found = _locator.Locate(config.Roblox.ProcessNames);

            return found.Count > 0
                ? DoctorCheck.Pass(name, $"{found.Count}개 발견")
                : DoctorCheck.Warn(name, "설치되어 있지 않음",
                    "Roblox 를 설치하면 자동으로 탐지됩니다. 방화벽 차단만 해당되며 DNS 차단은 그대로 동작합니다.");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn(name, ex.GetType().Name);
        }
    }

    // ==================================================== Telegram

    private static DoctorCheck CheckTelegramConfigured(TimeBlockerConfig config)
    {
        const string name = "Telegram configured";

        if (!config.Telegram.Enabled)
        {
            return DoctorCheck.Warn(name, "사용 안 함",
                "TimeBlocker.Service.exe enable-telegram 으로 켤 수 있습니다.");
        }

        return string.IsNullOrWhiteSpace(config.Telegram.ProtectedBotToken)
            ? DoctorCheck.Fail(name, "Bot Token 없음",
                "TimeBlocker.Service.exe set-token <BotToken> 을 실행하세요.")
            : DoctorCheck.Pass(name, "Bot Token 저장됨");
    }

    private static DoctorCheck CheckTelegramAdminIds(TimeBlockerConfig config)
    {
        const string name = "Telegram admin ID";

        if (!config.Telegram.Enabled) return DoctorCheck.Pass(name, "해당 없음");

        return config.Telegram.AllowedUserIds.Count > 0
            ? DoctorCheck.Pass(name, string.Join(", ", config.Telegram.AllowedUserIds))
            : DoctorCheck.Fail(name, "설정되지 않음",
                "TimeBlocker.Service.exe set-admin <TelegramUserId> 를 실행하세요. (아무도 명령할 수 없는 상태)");
    }

    private async Task<DoctorCheck> CheckTelegramApiAsync(TimeBlockerConfig config, CancellationToken ct)
    {
        const string name = "Telegram API";

        if (!config.Telegram.Enabled) return DoctorCheck.Pass(name, "해당 없음");

        var token = SecretProtector.Unprotect(config.Telegram.ProtectedBotToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return DoctorCheck.Fail(name, "토큰을 복호화하지 못함",
                "다른 PC 에서 복사한 설정일 수 있습니다. set-token 으로 다시 설정하세요.");
        }

        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            // getMe 는 부작용이 없는 확인용 API 다.
            using var response = await client
                .GetAsync($"https://api.telegram.org/bot{token}/getMe", cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return DoctorCheck.Fail(name, $"HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode == 401
                        ? "Bot Token 이 올바르지 않습니다. set-token 으로 다시 설정하세요."
                        : "인터넷 연결과 방화벽을 확인하세요.");
            }

            // 봇 이름만 뽑아 보여준다. 토큰은 절대 출력하지 않는다.
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var username = document.RootElement.TryGetProperty("result", out var result)
                           && result.TryGetProperty("username", out var name0)
                ? name0.GetString()
                : null;

            return DoctorCheck.Pass(name, username is null ? "연결 OK" : $"@{username}");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Fail(name, ex.GetType().Name,
                "인터넷 연결을 확인하세요. Telegram 이 안 되어도 로컬 차단은 정상 동작합니다.");
        }
    }

    // ==================================================== 파일 / 권한

    private DoctorCheck CheckConfigValidation()
    {
        const string name = "Config file";

        try
        {
            var path = AppPaths.ConfigFile;
            if (!File.Exists(path)) return DoctorCheck.Warn(name, "없음 (기본 설정으로 동작)");

            var json = File.ReadAllText(path);
            var config = JsonUtil.Deserialize<TimeBlockerConfig>(json);

            if (config is null)
            {
                return DoctorCheck.Fail(name, "해석 실패",
                    $"{path} 를 지우면 기본 설정으로 다시 만들어집니다.");
            }

            config.Normalize();

            var problems = new List<string>();
            if (config.YouTube.Domains.Count == 0 && config.YouTube.Enabled) problems.Add("YouTube 도메인 없음");
            if (config.Roblox.Domains.Count == 0 && config.Roblox.Enabled) problems.Add("Roblox 도메인 없음");
            if (config.Dns.UpstreamServers.Count == 0) problems.Add("상위 DNS 없음");

            if (File.Exists(path + ".broken"))
            {
                problems.Add("이전에 손상된 설정이 백업되어 있음(.broken)");
            }

            return problems.Count == 0
                ? DoctorCheck.Pass(name, "정상")
                : DoctorCheck.Warn(name, string.Join(", ", problems), "설정을 확인하세요.");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Fail(name, ex.GetType().Name,
                $"{AppPaths.ConfigFile} 를 지우면 기본 설정으로 다시 만들어집니다.");
        }
    }

    private static DoctorCheck CheckStateReadWrite()
    {
        const string name = "State read/write";

        try
        {
            var directory = Path.Combine(AppPaths.RootDirectory, "state");
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ".doctor-probe");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
            _ = File.ReadAllText(probe);
            File.Delete(probe);

            return DoctorCheck.Pass(name, directory);
        }
        catch (Exception ex)
        {
            return DoctorCheck.Fail(name, ex.GetType().Name,
                "서비스를 관리자(LocalSystem) 권한으로 실행하고 있는지 확인하세요.");
        }
    }

    private static DoctorCheck CheckDataAcl()
    {
        const string name = "Data directory ACL";

        try
        {
            var directory = AppPaths.RootDirectory;
            if (!Directory.Exists(directory)) return DoctorCheck.Warn(name, "폴더 없음");

            var security = new DirectoryInfo(directory).GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            // 일반 사용자에게 쓰기 권한이 열려 있으면 자녀 계정이 설정을 고칠 수 있다.
            var usersCanWrite = security
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow
                    && rule.IdentityReference.Equals(users)
                    && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Modify
                                                 | FileSystemRights.FullControl)) != 0);

            return usersCanWrite
                ? DoctorCheck.Warn(name, "일반 사용자에게 쓰기 권한 있음",
                    "서비스를 관리자 권한으로 재시작하면 권한이 자동으로 정리됩니다.")
                : DoctorCheck.Pass(name, "SYSTEM/Administrators 만 쓰기");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn(name, ex.GetType().Name);
        }
    }

    private static DoctorCheck CheckLogDirectory()
    {
        const string name = "Log directory";

        try
        {
            var directory = AppPaths.LogDirectory;
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ".doctor-probe");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);

            return DoctorCheck.Pass(name, directory);
        }
        catch (Exception ex)
        {
            return DoctorCheck.Fail(name, ex.GetType().Name,
                "로그를 남길 수 없습니다. 폴더 권한을 확인하세요.");
        }
    }

    // ==================================================== DNS 질의 헬퍼

    private sealed record DnsAnswer(int Rcode, int AnswerCount, int AuthorityCount);

    private async Task<DnsAnswer?> QueryAsync(string domain, IPAddress server, int port, CancellationToken ct)
    {
        try
        {
            var query = BuildQuery(domain);

            using var client = new UdpClient(server.AddressFamily);
            await client.SendAsync(query, query.Length, new IPEndPoint(server, port)).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DnsQueryTimeoutMs);

            var result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length < 12) return null;

            return new DnsAnswer(buffer[3] & 0x0F, (buffer[6] << 8) | buffer[7], (buffer[8] << 8) | buffer[9]);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] BuildQuery(string domain)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var bytes = new List<byte>
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0x00);
        bytes.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 });
        return bytes.ToArray();
    }
}
