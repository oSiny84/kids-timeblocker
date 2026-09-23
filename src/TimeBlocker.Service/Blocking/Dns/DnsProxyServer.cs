using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Configuration;

namespace TimeBlocker.Service.Blocking.Dns;

public interface IDnsProxyServer
{
    bool IsRunning { get; }

    /// <summary>마지막 시작/동작 실패 이유. 성공하면 null. 민감정보를 담지 않는다.</summary>
    string? LastError { get; }

    /// <summary>IPv6 루프백(::1)에서도 질의를 받고 있는지. 어댑터 IPv6 DNS 변경 가능 여부를 결정한다.</summary>
    bool Ipv6ListenerActive { get; }

    /// <summary>차단할 도메인 집합을 갱신한다. (즉시 반영, 재시작 불필요)</summary>
    void UpdateBlockedDomains(IReadOnlyCollection<string> domains);

    /// <summary>시작에 성공하면 true. 실패해도 예외를 던지지 않는다.</summary>
    Task<bool> StartAsync(DnsSettings settings, CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>
/// 127.0.0.1 에서 도는 아주 단순한 UDP DNS 프록시.
///
///   Application -> 127.0.0.1:53 -> 차단 대상인가?
///                                   YES -> NXDOMAIN
///                                   NO  -> Upstream DNS 로 전달 후 응답 중계
///
/// hosts 방식과 달리 하위 도메인(rr1---sn-xxx.googlevideo.com)까지 막을 수 있는 것이 장점이다.
/// 53 포트를 열려면 관리자 권한이 필요하다.
/// </summary>
public sealed class DnsProxyServer : IDnsProxyServer, IAsyncDisposable
{
    private readonly ILogger<DnsProxyServer> _logger;
    private readonly object _lock = new();

    // IPv4(127.0.0.1)와 IPv6(::1) 양쪽에서 받는다.
    // IPv4 만 받으면 Windows 가 IPv6 DNS 경로로 질의해 프록시를 우회할 수 있다.
    private readonly List<UdpClient> _listeners = new();
    private readonly List<Task> _receiveLoops = new();
    private CancellationTokenSource? _cts;
    private IReadOnlySet<string> _blocked = new HashSet<string>();
    private IPEndPoint[] _upstreams = Array.Empty<IPEndPoint>();
    private int _upstreamTimeoutMs = 3000;

    public DnsProxyServer(ILogger<DnsProxyServer> logger)
    {
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public bool Ipv6ListenerActive { get; private set; }

    public void UpdateBlockedDomains(IReadOnlyCollection<string> domains)
    {
        // 프록시는 정확히 일치뿐 아니라 하위 도메인까지 막는다.
        // 여기서는 비교용으로 정규화만 하고, 판정은 DnsMessage.IsBlocked 가 한다.
        var set = new HashSet<string>(
            domains.Select(DnsMessage.Normalize).Where(d => d.Length > 0),
            StringComparer.Ordinal);

        lock (_lock) _blocked = set;
    }

    /// <summary>
    /// 프록시를 시작한다.
    /// 어떤 이유로 실패해도 예외를 밖으로 던지지 않는다. false 를 돌려주면 호출측이 hosts 로 폴백한다.
    /// </summary>
    public Task<bool> StartAsync(DnsSettings settings, CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.FromResult(true);

        try
        {
            _upstreams = ParseUpstreams(settings.UpstreamServers);
            _upstreamTimeoutMs = settings.UpstreamTimeoutMs;

            if (_upstreams.Length == 0)
            {
                LastError = "No upstream DNS server configured";
                _logger.LogError("상위 DNS 서버가 하나도 설정되지 않아 DNS 프록시를 시작할 수 없습니다.");
                return Task.FromResult(false);
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 루프백에만 바인딩한다. 외부에서 이 DNS 를 쓸 수 없게 하기 위함이다.
            // IPv4 는 필수, IPv6 는 열리면 함께 사용한다.
            var ipv4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, settings.ProxyPort));
            _listeners.Add(ipv4);
            _receiveLoops.Add(Task.Run(() => ReceiveLoopAsync(ipv4, _cts.Token), CancellationToken.None));

            Ipv6ListenerActive = TryAddIpv6Listener(settings.ProxyPort, _cts.Token);

            IsRunning = true;
            LastError = null;

            _logger.LogInformation(
                "DNS 프록시 시작: 127.0.0.1:{Port}{Ipv6Text} (상위 DNS: {Upstreams})",
                settings.ProxyPort,
                Ipv6ListenerActive ? ", [::1] 포함" : " (IPv6 미사용)",
                string.Join(", ", _upstreams.Select(u => u.Address.ToString())));

            return Task.FromResult(true);
        }
        catch (SocketException ex)
        {
            LastError = $"Cannot bind 127.0.0.1:{settings.ProxyPort} ({ex.SocketErrorCode})";
            _logger.LogError(ex,
                "DNS 프록시가 127.0.0.1:{Port} 를 열지 못했습니다. 다른 프로그램이 53 포트를 쓰고 있거나 권한이 없을 수 있습니다.",
                settings.ProxyPort);
        }
        catch (Exception ex)
        {
            // 예상 못 한 오류로 서비스가 죽으면 안 된다. 폴백에 맡긴다.
            LastError = $"Proxy start failed ({ex.GetType().Name})";
            _logger.LogError(ex, "DNS 프록시 시작 중 예기치 않은 오류가 발생했습니다.");
        }

        // 부분적으로 열린 자원을 정리한다.
        SafeCleanup();
        IsRunning = false;
        return Task.FromResult(false);
    }

    /// <summary>
    /// IPv6 루프백에서도 받도록 시도한다.
    /// 실패해도 치명적이지 않지만, 그 경우 어댑터 IPv6 DNS 를 ::1 로 바꾸면 안 되므로
    /// 결과를 Ipv6ListenerActive 로 알린다.
    /// </summary>
    private bool TryAddIpv6Listener(int port, CancellationToken ct)
    {
        if (!Socket.OSSupportsIPv6)
        {
            _logger.LogDebug("이 시스템은 IPv6 를 지원하지 않아 IPv6 리스너를 만들지 않습니다.");
            return false;
        }

        try
        {
            var ipv6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, port));
            _listeners.Add(ipv6);
            _receiveLoops.Add(Task.Run(() => ReceiveLoopAsync(ipv6, ct), CancellationToken.None));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "IPv6 루프백 포트({Port})를 열지 못했습니다. 어댑터 IPv6 DNS 는 변경하지 않습니다.", port);
            return false;
        }
    }

    private void SafeCleanup()
    {
        try { _cts?.Cancel(); } catch { /* 무시 */ }
        try { _cts?.Dispose(); } catch { /* 무시 */ }
        _cts = null;

        foreach (var listener in _listeners)
        {
            try { listener.Dispose(); } catch { /* 무시 */ }
        }
        _listeners.Clear();
        _receiveLoops.Clear();
        Ipv6ListenerActive = false;
    }

    /// <summary>프록시를 중지한다. 정리 중 오류가 나도 예외를 던지지 않는다.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        try
        {
            try { _cts?.Cancel(); } catch { /* 무시 */ }

            // 리스너를 닫아 ReceiveAsync 를 깨운다.
            foreach (var listener in _listeners)
            {
                try { listener.Dispose(); } catch { /* 무시 */ }
            }

            var loops = _receiveLoops.ToArray();
            _listeners.Clear();
            _receiveLoops.Clear();
            Ipv6ListenerActive = false;

            if (loops.Length > 0)
            {
                try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { /* 무시 */ }
            }

            try { _cts?.Dispose(); } catch { /* 무시 */ }
            _cts = null;

            _logger.LogInformation("DNS 프록시를 중지했습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS 프록시 정리 중 오류가 발생했지만 계속 진행합니다.");
        }
    }

    /// <summary>
    /// 수신 루프. 치명적 오류가 나면 IsRunning 을 내리고 LastError 를 남긴다.
    /// 그러면 다음 적용 주기에서 BlockingCoordinator 가 hosts 폴백으로 전환한다.
    /// 여기서 발생한 예외가 서비스 전체를 죽이지 않도록 반드시 안에서 처리한다.
    /// </summary>
    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken ct)
    {
        try
        {
            await ReceiveLoopCoreAsync(listener, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            LastError = $"Proxy receive loop stopped ({ex.GetType().Name})";
            _logger.LogError(ex, "DNS 프록시 수신 루프가 예기치 않게 중단되었습니다. hosts 폴백으로 전환됩니다.");
        }
    }

    private async Task ReceiveLoopCoreAsync(UdpClient listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // 클라이언트가 응답 전에 사라진 경우 등. 루프는 계속 돈다.
                _logger.LogDebug(ex, "DNS 수신 중 소켓 오류");
                continue;
            }

            // 질의 하나가 느려도 다른 질의를 막지 않도록 별도 작업으로 처리한다.
            _ = Task.Run(() => HandleQueryAsync(listener, received, ct), CancellationToken.None);
        }
    }

    private async Task HandleQueryAsync(UdpClient listener, UdpReceiveResult received, CancellationToken ct)
    {
        try
        {
            var query = received.Buffer;
            var name = DnsMessage.TryReadQuestionName(query);

            IReadOnlySet<string> blocked;
            lock (_lock) blocked = _blocked;

            if (name is not null && DnsMessage.IsBlocked(name, blocked))
            {
                var nxdomain = DnsMessage.TryBuildNxDomainResponse(query);
                if (nxdomain is not null)
                {
                    await SendAsync(listener, nxdomain, received.RemoteEndPoint).ConfigureAwait(false);
                    _logger.LogDebug("DNS 차단: {Name}", name);
                    return;
                }
            }

            var answer = await ForwardAsync(query, ct).ConfigureAwait(false);
            if (answer is not null)
            {
                await SendAsync(listener, answer, received.RemoteEndPoint).ConfigureAwait(false);
                return;
            }

            // 상위 DNS 전부 실패: 클라이언트를 기다리게 두지 말고 SERVFAIL 을 돌려준다.
            var servfail = DnsMessage.TryBuildServerFailureResponse(query);
            if (servfail is not null) await SendAsync(listener, servfail, received.RemoteEndPoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DNS 질의 처리 실패");
        }
    }

    private async Task SendAsync(UdpClient listener, byte[] data, IPEndPoint destination)
    {
        try
        {
            await listener.SendAsync(data, data.Length, destination).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // 클라이언트가 이미 사라진 경우. 무시한다.
        }
    }

    /// <summary>상위 DNS 로 순서대로 시도한다. 하나라도 응답하면 그것을 돌려준다.</summary>
    private async Task<byte[]?> ForwardAsync(byte[] query, CancellationToken ct)
    {
        foreach (var upstream in _upstreams)
        {
            try
            {
                using var client = new UdpClient(upstream.AddressFamily);
                await client.SendAsync(query, query.Length, upstream).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_upstreamTimeoutMs);

                var result = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                return result.Buffer;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                // 이 상위 DNS 는 실패. 다음 것을 시도한다.
                _logger.LogDebug(ex, "상위 DNS 질의 실패: {Upstream}", upstream);
            }
        }

        _logger.LogWarning("모든 상위 DNS 질의에 실패했습니다. 인터넷 연결을 확인하세요.");
        return null;
    }

    private IPEndPoint[] ParseUpstreams(IEnumerable<string> servers)
    {
        var endpoints = new List<IPEndPoint>();
        foreach (var server in servers)
        {
            var text = server.Trim();
            if (text.Length == 0) continue;

            // "1.1.1.1" 또는 "1.1.1.1:53"
            var port = 53;
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsedPort))
            {
                port = parsedPort;
                text = text[..colon];
            }

            if (IPAddress.TryParse(text, out var address)) endpoints.Add(new IPEndPoint(address, port));
            else _logger.LogWarning("상위 DNS 주소를 해석하지 못했습니다: {Server}", server);
        }
        return endpoints.ToArray();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
