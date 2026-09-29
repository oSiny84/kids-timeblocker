using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Ipc;
using TimeBlocker.Shared.Notifications;

namespace TimeBlocker.Service.Blocking;

public interface IUserAlertService
{
    /// <summary>PC 앞의 사용자에게 알린다. 실제로 표시된 곳의 수를 돌려준다. 0 이면 아무도 못 봤다.</summary>
    int Alert(NotificationKind kind, string title, string body, bool allowReply = true);
}

/// <summary>
/// 알림을 어디로 보낼지 고른다.
///
///   1순위: 트레이 앱 (답장 가능, 보기 좋음)
///   2순위: Windows 기본 메시지 창 (트레이 앱이 없거나 죽었을 때)
///
/// 트레이 앱이 설치되지 않았거나 아이가 종료해도 경고가 사라지면 안 되므로
/// 폴백을 반드시 유지한다.
/// </summary>
public sealed class UserAlertService : IUserAlertService
{
    private readonly INotificationHub _hub;
    private readonly IUserSessionNotifier _fallback;
    private readonly ILogger<UserAlertService> _logger;

    public UserAlertService(
        INotificationHub hub,
        IUserSessionNotifier fallback,
        ILogger<UserAlertService> logger)
    {
        _hub = hub;
        _fallback = fallback;
        _logger = logger;
    }

    public int Alert(NotificationKind kind, string title, string body, bool allowReply = true)
    {
        if (_hub.HasClients)
        {
            try
            {
                var envelope = new NotificationEnvelope
                {
                    Kind = kind,
                    Title = title,
                    Body = body,
                    AllowReply = allowReply
                };

                // 호출자(정책 평가 루프)가 여기서 멈추면 안 되므로 짧게 기다리고 넘어간다.
                var sent = _hub.BroadcastAsync(envelope).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (sent > 0) return sent;

                _logger.LogDebug("트레이 앱에 보내지 못했습니다. 기본 메시지 창으로 대체합니다.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "트레이 앱 알림 실패. 기본 메시지 창으로 대체합니다.");
            }
        }

        return _fallback.Notify(title, body);
    }
}
