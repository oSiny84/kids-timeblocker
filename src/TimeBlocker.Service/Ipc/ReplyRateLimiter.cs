using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Notifications;

namespace TimeBlocker.Service.Ipc;

/// <summary>
/// 답장 도배를 막는다.
///
/// 알림 파이프는 표준 사용자도 연결할 수 있으므로, 연결한 쪽이 관리자에게
/// 메시지를 무한정 보낼 수 있으면 안 된다. 쿨다운과 시간당 상한을 둔다.
/// </summary>
public sealed class ReplyRateLimiter
{
    private readonly ISystemClock _clock;
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _recent = new();

    private DateTimeOffset _lastUtc = DateTimeOffset.MinValue;

    public ReplyRateLimiter(ISystemClock clock)
    {
        _clock = clock;
    }

    /// <summary>보내도 되면 true. 그때만 사용량을 차감한다.</summary>
    public bool TryConsume(out string reason)
    {
        var nowUtc = _clock.UtcNow;

        lock (_lock)
        {
            if (nowUtc - _lastUtc < TimeSpan.FromSeconds(NotifierProtocol.ReplyCooldownSeconds))
            {
                reason = $"{NotifierProtocol.ReplyCooldownSeconds}초 쿨다운";
                return false;
            }

            // 1시간이 지난 기록은 버린다.
            while (_recent.Count > 0 && nowUtc - _recent.Peek() > TimeSpan.FromHours(1))
            {
                _recent.Dequeue();
            }

            if (_recent.Count >= NotifierProtocol.ReplyPerHourLimit)
            {
                reason = $"시간당 {NotifierProtocol.ReplyPerHourLimit}건 초과";
                return false;
            }

            _lastUtc = nowUtc;
            _recent.Enqueue(nowUtc);
            reason = string.Empty;
            return true;
        }
    }

    /// <summary>줄바꿈을 없애 로그/메시지 형식이 깨지지 않게 한다.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    /// <summary>길이 상한을 넘으면 자른다.</summary>
    public static string Truncate(string text)
    {
        if (text.Length <= NotifierProtocol.MaxReplyLength) return text;
        return text[..NotifierProtocol.MaxReplyLength] + "...";
    }
}
