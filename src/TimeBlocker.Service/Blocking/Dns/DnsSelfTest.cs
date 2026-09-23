using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Blocking.Dns;

public interface IDnsSelfTest
{
    /// <summary>
    /// 프록시에 직접 질의해서 정상 동작하는지 확인한다. (어댑터 DNS 를 바꾸기 전에 수행)
    /// </summary>
    Task<DnsSelfTestResult> TestProxyDirectAsync(
        int port, string allowedDomain, string? blockedDomain, CancellationToken ct);

    /// <summary>
    /// 시스템 확인자(resolver)를 통해 질의해서, 어댑터 DNS 변경이 실제로 먹었는지 확인한다.
    /// (어댑터 DNS 를 바꾼 뒤에 수행)
    /// </summary>
    Task<DnsSelfTestResult> TestThroughSystemResolverAsync(
        string allowedDomain, string? blockedDomain, CancellationToken ct);
}

/// <summary>
/// DNS 경로가 실제로 살아 있는지 확인한다.
///
/// 어댑터 DNS 를 127.0.0.1 로 바꾸는 것은 PC 인터넷을 끊어버릴 수 있는 작업이므로,
/// "프록시가 진짜 응답하는가" 를 바꾸기 전에 반드시 확인하고,
/// 바꾼 뒤에도 "실제 이름 해석이 되는가" 를 다시 확인한다.
/// </summary>
public sealed class DnsSelfTest : IDnsSelfTest
{
    /// <summary>self-test 는 빨리 끝나야 한다. 부팅/적용 경로를 오래 막으면 안 된다.</summary>
    private const int QueryTimeoutMs = 4000;

    private readonly ILogger<DnsSelfTest> _logger;

    public DnsSelfTest(ILogger<DnsSelfTest> logger)
    {
        _logger = logger;
    }

    // ------------------------------------------------- 프록시 직접 질의

    public async Task<DnsSelfTestResult> TestProxyDirectAsync(
        int port, string allowedDomain, string? blockedDomain, CancellationToken ct)
    {
        // 1) 허용 도메인: 프록시가 상위 DNS 로 전달해서 답을 가져와야 한다.
        var allowed = await QueryAsync(allowedDomain, port, ct).ConfigureAwait(false);

        if (allowed is null)
        {
            return DnsSelfTestResult.Fail($"프록시가 응답하지 않습니다 ({allowedDomain})");
        }

        // SERVFAIL(2) 은 프록시는 살아 있지만 상위 DNS 에 닿지 못한 경우다. (인터넷 끊김)
        var upstreamDown = allowed.Rcode == 2;

        // 2) 차단 도메인: 우리가 만든 NXDOMAIN 이어야 한다.
        //    우리 응답은 답변/권한 레코드가 모두 비어 있다는 점으로 구분한다.
        var blockedOk = true;
        if (!string.IsNullOrWhiteSpace(blockedDomain))
        {
            var blocked = await QueryAsync(blockedDomain!, port, ct).ConfigureAwait(false);
            blockedOk = blocked is not null && IsOurBlockResponse(blocked);

            if (!blockedOk)
            {
                return new DnsSelfTestResult
                {
                    Success = false,
                    AllowedDomainResolved = allowed.Rcode == 0,
                    BlockedDomainBlocked = false,
                    UpstreamUnreachable = upstreamDown,
                    Detail = $"차단 도메인이 차단되지 않았습니다 ({blockedDomain})"
                };
            }
        }

        if (upstreamDown)
        {
            // 프록시 자체는 정상이다. 인터넷이 끊긴 것이므로 이것만으로 롤백하지는 않는다.
            _logger.LogWarning("DNS self-test: 프록시는 응답하지만 상위 DNS 에 닿지 못했습니다. (인터넷 연결 확인 필요)");
            return new DnsSelfTestResult
            {
                Success = true,
                AllowedDomainResolved = false,
                BlockedDomainBlocked = blockedOk,
                UpstreamUnreachable = true,
                Detail = "상위 DNS 응답 없음 (인터넷 끊김)"
            };
        }

        return new DnsSelfTestResult
        {
            Success = true,
            AllowedDomainResolved = allowed.Rcode == 0,
            BlockedDomainBlocked = blockedOk,
            UpstreamUnreachable = false,
            Detail = "OK"
        };
    }

    // --------------------------------------------- 시스템 확인자 경유 질의

    public async Task<DnsSelfTestResult> TestThroughSystemResolverAsync(
        string allowedDomain, string? blockedDomain, CancellationToken ct)
    {
        // 차단 도메인이 시스템 확인자에서도 막히는지 확인한다.
        // 이것이 통과해야 "어댑터 DNS 변경이 실제로 먹었다" 고 말할 수 있다.
        var blockedOk = true;
        if (!string.IsNullOrWhiteSpace(blockedDomain))
        {
            blockedOk = !await ResolvesAsync(blockedDomain!, ct).ConfigureAwait(false);
        }

        // 허용 도메인도 항상 확인한다.
        // (차단 검사에서 일찍 빠져나가면 진단 출력에 "확인하지 않음"과 "실패"가 구분되지 않는다)
        var allowedOk = await ResolvesAsync(allowedDomain, ct).ConfigureAwait(false);

        if (!blockedOk)
        {
            return new DnsSelfTestResult
            {
                Success = false,
                AllowedDomainResolved = allowedOk,
                BlockedDomainBlocked = false,
                Detail = $"어댑터 DNS 변경 후에도 차단 도메인이 해석됩니다 ({blockedDomain})"
            };
        }

        return new DnsSelfTestResult
        {
            Success = true, // 허용 도메인 실패는 호출측이 상위 DNS 상태와 함께 판단한다.
            AllowedDomainResolved = allowedOk,
            BlockedDomainBlocked = true,
            Detail = allowedOk ? "OK" : $"허용 도메인을 해석하지 못했습니다 ({allowedDomain})"
        };
    }

    private async Task<bool> ResolvesAsync(string domain, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(QueryTimeoutMs);

            var addresses = await System.Net.Dns.GetHostAddressesAsync(domain, timeoutCts.Token).ConfigureAwait(false);
            return addresses.Length > 0 && !addresses.All(IPAddress.IsLoopback);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "시스템 확인자 질의 실패: {Domain}", domain);
            return false;
        }
    }

    // ------------------------------------------------------ DNS 패킷 처리

    private sealed record DnsAnswer(int Rcode, int AnswerCount, int AuthorityCount);

    /// <summary>
    /// 우리가 만든 차단 응답인지 판단한다.
    /// TimeBlocker 는 NXDOMAIN 에 어떤 레코드도 넣지 않는다.
    /// 반면 실제 상위 DNS 의 NXDOMAIN 응답에는 보통 SOA(권한 레코드)가 들어 있다.
    /// </summary>
    private static bool IsOurBlockResponse(DnsAnswer answer) =>
        answer.Rcode == 3 && answer.AnswerCount == 0 && answer.AuthorityCount == 0;

    private async Task<DnsAnswer?> QueryAsync(string domain, int port, CancellationToken ct)
    {
        try
        {
            var query = BuildQuery(domain);

            using var client = new UdpClient(AddressFamily.InterNetwork);
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            await client.SendAsync(query, query.Length, endpoint).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(QueryTimeoutMs);

            var result = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length < 12) return null;

            return new DnsAnswer(
                Rcode: buffer[3] & 0x0F,
                AnswerCount: (buffer[6] << 8) | buffer[7],
                AuthorityCount: (buffer[8] << 8) | buffer[9]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "프록시 직접 질의 실패: {Domain}", domain);
            return null;
        }
    }

    /// <summary>표준 A 레코드 질의 패킷을 만든다.</summary>
    private static byte[] BuildQuery(string domain)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);

        var bytes = new List<byte>
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00,   // 표준 질의, RD=1
            0x00, 0x01,   // QDCOUNT = 1
            0x00, 0x00,   // ANCOUNT
            0x00, 0x00,   // NSCOUNT
            0x00, 0x00    // ARCOUNT
        };

        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0x00);
        bytes.AddRange(new byte[] { 0x00, 0x01 }); // QTYPE = A
        bytes.AddRange(new byte[] { 0x00, 0x01 }); // QCLASS = IN

        return bytes.ToArray();
    }
}
