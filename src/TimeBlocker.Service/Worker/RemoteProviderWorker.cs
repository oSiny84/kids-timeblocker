using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Remote;

namespace TimeBlocker.Service.Worker;

/// <summary>
/// 원격 명령 제공자(Telegram)를 별도 백그라운드 작업으로 돌린다.
///
/// 핵심: 이 작업이 실패하거나 멈춰도 EnforcementWorker 의 차단 기능에는 영향이 없다.
/// 원격 제어와 핵심 차단 기능을 느슨하게 결합하기 위한 분리다.
/// </summary>
public sealed class RemoteProviderWorker : BackgroundService
{
    private readonly IEnumerable<IRemoteCommandProvider> _providers;
    private readonly ILogger<RemoteProviderWorker> _logger;

    public RemoteProviderWorker(
        IEnumerable<IRemoteCommandProvider> providers,
        ILogger<RemoteProviderWorker> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = _providers.Select(provider => RunProviderAsync(provider, stoppingToken)).ToArray();
        if (tasks.Length == 0)
        {
            _logger.LogInformation("등록된 원격 명령 제공자가 없습니다.");
            return;
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunProviderAsync(IRemoteCommandProvider provider, CancellationToken ct)
    {
        _logger.LogInformation("원격 명령 제공자 시작: {Provider}", provider.Name);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await provider.RunAsync(ct).ConfigureAwait(false);
                return; // 정상 종료(취소)
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 제공자가 예외로 빠져나와도 서비스는 살아 있어야 한다. 잠시 후 재시작한다.
                _logger.LogError(ex, "{Provider} 제공자가 중단되었습니다. 30초 후 다시 시작합니다.", provider.Name);

                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
