using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Core;

public sealed class PermitResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public TemporaryPermit? Permit { get; init; }

    public static PermitResult Fail(string message) => new() { Success = false, Message = message };

    public static PermitResult Ok(TemporaryPermit permit, string message) =>
        new() { Success = true, Message = message, Permit = permit };
}

public interface ITemporaryPermitManager
{
    /// <summary>일시 허용 부여. 같은 대상의 기존 허용은 누적되지 않고 교체된다.</summary>
    PermitResult Grant(BlockTarget target, int minutes, string source);

    /// <summary>모든 일시 허용을 즉시 취소한다. (lock)</summary>
    int CancelAll(string source);

    /// <summary>
    /// 특정 대상의 일시 허용만 취소한다. (block / unblock / auto 가 호출한다)
    /// ALL 허용이 걸려 있으면 그 대상만 빠지도록 나머지 대상에 동일 만료의 개별 허용을 만들어 준다.
    /// </summary>
    int Cancel(BlockTarget target, string source);

    /// <summary>지정 대상에 현재 적용되는 허용(가장 늦게 끝나는 것). 없으면 null.</summary>
    TemporaryPermit? GetEffective(BlockTarget target);

    /// <summary>만료되지 않은 모든 허용.</summary>
    IReadOnlyList<TemporaryPermit> GetActive();

    /// <summary>만료된 항목을 정리한다. 정리된 개수를 반환.</summary>
    int PurgeExpired();
}

/// <summary>
/// 일시 허용 관리.
///
/// 핵심 규칙:
///  - 만료는 항상 UTC 절대시각(ExpireTimeUtc)으로 저장한다.
///    => 서비스 재시작/재부팅 후 남은 시간을 다시 계산하지 않고 그대로 복원된다.
///    => "상대 시간 누적"으로 허용이 계속 늘어나는 문제가 생기지 않는다.
///  - 같은 대상에 대한 새 허용은 기존 것을 교체한다. (덧붙이지 않음)
/// </summary>
public sealed class TemporaryPermitManager : ITemporaryPermitManager
{
    private readonly IPermitStateStore _store;
    private readonly ISystemClock _clock;
    private readonly Func<TemporaryPermitSettings> _settingsProvider;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private readonly List<TemporaryPermit> _permits;

    public TemporaryPermitManager(
        IPermitStateStore store,
        ISystemClock clock,
        Func<TemporaryPermitSettings> settingsProvider,
        Action<string>? log = null)
    {
        _store = store;
        _clock = clock;
        _settingsProvider = settingsProvider;
        _log = log;

        // 시작 시 저장된 상태를 복원한다. (재부팅 복구)
        _permits = _store.Load();

        var removed = RemoveExpiredLocked(_clock.UtcNow);
        if (removed > 0) _store.Save(_permits);
    }

    public PermitResult Grant(BlockTarget target, int minutes, string source)
    {
        var settings = _settingsProvider() ?? new TemporaryPermitSettings();

        if (minutes < settings.MinMinutes)
        {
            return PermitResult.Fail($"Minutes must be at least {settings.MinMinutes}.");
        }

        if (minutes > settings.MaxMinutes)
        {
            return PermitResult.Fail($"Max permit is {settings.MaxMinutes} minutes. (requested: {minutes})");
        }

        var now = _clock.UtcNow;
        var permit = new TemporaryPermit
        {
            Target = target,
            StartTimeUtc = now,
            ExpireTimeUtc = now.AddMinutes(minutes),
            Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source
        };

        lock (_lock)
        {
            RemoveExpiredLocked(now);
            // 같은 대상의 기존 허용은 교체한다.
            _permits.RemoveAll(p => p.Target == target);
            _permits.Add(permit);
            _store.Save(_permits);
        }

        var localExpire = permit.ExpireTimeUtc.ToLocalTime();
        _log?.Invoke($"일시 허용: {target.ToDisplayName()} {minutes}분 (요청자: {permit.Source}, 만료: {localExpire:yyyy-MM-dd HH:mm})");

        return PermitResult.Ok(permit, $"{target.ToDisplayName()} allowed for {minutes} minutes. Expire: {localExpire:HH:mm}");
    }

    public int CancelAll(string source)
    {
        int count;
        lock (_lock)
        {
            count = _permits.Count;
            _permits.Clear();
            _store.Save(_permits);
        }

        if (count > 0) _log?.Invoke($"일시 허용 전체 취소 ({count}건, 요청자: {source})");
        return count;
    }

    public int Cancel(BlockTarget target, string source)
    {
        var now = _clock.UtcNow;
        int removed;

        lock (_lock)
        {
            RemoveExpiredLocked(now);

            // 이 대상에 적용되던 살아 있는 허용들
            var covering = _permits.Where(p => p.Covers(target) && p.IsActiveAt(now)).ToList();
            removed = covering.Count;
            if (removed == 0) return 0;

            foreach (var permit in covering)
            {
                _permits.Remove(permit);

                // ALL 허용이었다면, 취소 대상이 아닌 나머지는 계속 허용되어야 하므로
                // 남은 대상에 같은 만료시각의 개별 허용을 만들어 둔다.
                if (permit.Target != BlockTarget.All) continue;

                foreach (var other in BlockTargets.Real.Where(t => t != target))
                {
                    if (_permits.Any(p => p.Target == other && p.ExpireTimeUtc >= permit.ExpireTimeUtc)) continue;

                    _permits.RemoveAll(p => p.Target == other);
                    _permits.Add(new TemporaryPermit
                    {
                        Target = other,
                        StartTimeUtc = permit.StartTimeUtc,
                        ExpireTimeUtc = permit.ExpireTimeUtc,
                        Source = permit.Source
                    });
                }
            }

            _store.Save(_permits);
        }

        _log?.Invoke($"일시 허용 취소: {target.ToDisplayName()} (요청자: {source})");
        return removed;
    }

    public TemporaryPermit? GetEffective(BlockTarget target)
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            return _permits
                .Where(p => p.Covers(target) && p.IsActiveAt(now))
                .OrderByDescending(p => p.ExpireTimeUtc)
                .Select(p => p.Clone())
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<TemporaryPermit> GetActive()
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            return _permits
                .Where(p => p.IsActiveAt(now))
                .OrderBy(p => p.ExpireTimeUtc)
                .Select(p => p.Clone())
                .ToList();
        }
    }

    public int PurgeExpired()
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            var removed = RemoveExpiredLocked(now);
            if (removed > 0) _store.Save(_permits);
            return removed;
        }
    }

    private int RemoveExpiredLocked(DateTimeOffset now)
    {
        var expired = _permits.Where(p => !p.IsActiveAt(now)).ToList();
        foreach (var permit in expired)
        {
            _permits.Remove(permit);
            _log?.Invoke($"일시 허용 만료: {permit.Target.ToDisplayName()}");
        }
        return expired.Count;
    }
}
