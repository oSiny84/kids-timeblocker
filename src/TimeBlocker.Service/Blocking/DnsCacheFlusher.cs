using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Blocking;

public interface IDnsCacheFlusher
{
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// Windows DNS 확인자 캐시를 비운다.
///
/// 차단/허용이 바뀐 직후 예전 캐시 때문에 적용이 늦어지는 것을 막는다.
/// 우선 dnsapi.dll 의 DnsFlushResolverCache 를 직접 호출하고(프로세스 생성 없음),
/// 실패하면 ipconfig /flushdns 로 폴백한다.
/// </summary>
public sealed class DnsCacheFlusher : IDnsCacheFlusher
{
    private readonly ILogger<DnsCacheFlusher> _logger;

    public DnsCacheFlusher(ILogger<DnsCacheFlusher> logger)
    {
        _logger = logger;
    }

    // BOOL DnsFlushResolverCache(void); - 문서화되지 않았지만 Windows 전 버전에서 제공된다.
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();

    public async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            if (DnsFlushResolverCache())
            {
                _logger.LogDebug("DNS 캐시를 비웠습니다. (dnsapi)");
                return;
            }

            _logger.LogDebug("DnsFlushResolverCache 실패(코드 {Code}). ipconfig 로 재시도합니다.",
                Marshal.GetLastWin32Error());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "dnsapi.dll 호출을 사용할 수 없습니다. ipconfig 로 대체합니다.");
        }

        var result = await ProcessRunner.RunAsync("ipconfig", "/flushdns", _logger, cancellationToken: ct)
            .ConfigureAwait(false);

        if (result.Success) _logger.LogDebug("DNS 캐시를 비웠습니다. (ipconfig)");
        else _logger.LogWarning("DNS 캐시를 비우지 못했습니다. 적용이 최대 수십 초 늦어질 수 있습니다.");
    }
}
