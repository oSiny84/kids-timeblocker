using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;

namespace TimeBlocker.Service.Worker;

/// <summary>
/// 주기적으로 정책을 재계산하고 시스템에 반영하는 핵심 루프.
///
/// 하는 일:
///  1. 만료된 일시 허용 정리 (만료되면 별도 조작 없이 자동 재차단)
///  2. 정책 재계산 후 DNS / 방화벽 반영
///  3. 시스템 시간 급변 감지 후 로그 기록
/// </summary>
public sealed class EnforcementWorker : BackgroundService
{
    private readonly IConfigurationStore _configStore;
    private readonly ITemporaryPermitManager _permits;
    private readonly BlockingCoordinator _coordinator;
    private readonly ISystemClock _clock;
    private readonly ILogger<EnforcementWorker> _logger;

    // 시간 급변 감지용: 단조 증가하는 틱과 벽시계를 함께 기록해 둔다.
    private long _lastTickMs;
    private DateTimeOffset _lastWallClock;

    public EnforcementWorker(
        IConfigurationStore configStore,
        ITemporaryPermitManager permits,
        BlockingCoordinator coordinator,
        ISystemClock clock,
        ILogger<EnforcementWorker> logger)
    {
        _configStore = configStore;
        _permits = permits;
        _coordinator = coordinator;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TimeBlocker 차단 엔진을 시작합니다.");

        _lastTickMs = Environment.TickCount64;
        _lastWallClock = _clock.UtcNow;

        // 이전 실행이 비정상 종료되어 어댑터 DNS 가 127.0.0.1 로 남아 있으면
        // 먼저 안전 상태(원래 DNS)로 되돌린 뒤에 프록시를 다시 초기화한다.
        // 이 순서를 지키지 않으면 프록시가 뜨기 전까지 PC 인터넷이 끊긴 채로 있게 된다.
        await _coordinator.RecoverAtStartupAsync(stoppingToken).ConfigureAwait(false);

        // 부팅 직후 상태를 바로 맞춘다. (재부팅 후 복구)
        await _coordinator.ApplyNowAsync(stoppingToken).ConfigureAwait(false);
        LogRestoredPermits();

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(_configStore.Current.Enforcement.EvaluationIntervalSeconds);

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                DetectTimeJump(interval);

                // 만료된 허용을 정리한다. 이것만으로 다음 ApplyNowAsync 가 자동 재차단을 수행한다.
                _permits.PurgeExpired();

                await _coordinator.ApplyNowAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 어떤 오류도 루프를 멈추면 안 된다.
                _logger.LogError(ex, "차단 주기 처리 중 오류가 발생했습니다.");
            }
        }

        _logger.LogInformation("차단 엔진을 종료합니다.");
    }

    /// <summary>
    /// 사용자가 PC 시간을 바꿔 제한을 피하려는 상황을 감지한다.
    ///
    /// Environment.TickCount64 는 시스템 시간 변경의 영향을 받지 않으므로,
    /// "틱으로 잰 경과 시간"과 "벽시계로 잰 경과 시간"의 차이가 크면 시간이 변경된 것이다.
    ///
    /// 참고: 일시 허용은 상대 시간이 아니라 UTC 절대 만료시각으로 관리되므로
    /// 시간을 되돌려도 허용이 무한정 늘어나지는 않는다.
    /// </summary>
    private void DetectTimeJump(TimeSpan expectedInterval)
    {
        var nowTick = Environment.TickCount64;
        var nowWall = _clock.UtcNow;

        var tickElapsed = TimeSpan.FromMilliseconds(nowTick - _lastTickMs);
        var wallElapsed = nowWall - _lastWallClock;
        var drift = wallElapsed - tickElapsed;

        var threshold = TimeSpan.FromMinutes(_configStore.Current.Enforcement.TimeJumpThresholdMinutes);

        if (drift.Duration() >= threshold)
        {
            _logger.LogWarning(
                "시스템 시간이 비정상적으로 변경되었습니다. 변화량: {Drift} (예상 주기 {Interval}). 현재 시각: {Now}",
                drift,
                expectedInterval,
                _clock.LocalNow.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        _lastTickMs = nowTick;
        _lastWallClock = nowWall;
    }

    /// <summary>재시작/재부팅 후 복원된 허용을 로그로 남긴다.</summary>
    private void LogRestoredPermits()
    {
        foreach (var permit in _permits.GetActive())
        {
            _logger.LogInformation(
                "일시 허용 복원: {Target} (만료: {Expire})",
                permit.Target,
                permit.ExpireTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // graceful shutdown: 진행 중인 적용을 마치고 DNS 어댑터 설정을 되돌린다.
        await _coordinator.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
