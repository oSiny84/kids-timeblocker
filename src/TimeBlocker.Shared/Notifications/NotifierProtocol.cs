namespace TimeBlocker.Shared.Notifications;

/// <summary>
/// 서비스와 트레이 앱(TimeBlocker.Notifier) 사이의 통로.
///
/// 관리 명령용 파이프(TimeBlocker.Service)와 일부러 분리했다.
/// 이 통로는 표준 사용자(아이 계정)도 연결할 수 있어야 하므로,
/// 할 수 있는 일을 "알림 받기 / 답장 보내기" 둘로만 제한한다.
/// 설정을 바꾸거나 차단을 푸는 것은 이 통로로 절대 불가능하다.
/// </summary>
public static class NotifierProtocol
{
    public const string PipeName = "TimeBlocker.Notify";

    /// <summary>답장 1건의 최대 길이.</summary>
    public const int MaxReplyLength = 300;

    /// <summary>답장 사이의 최소 간격(초). 도배를 막는다.</summary>
    public const int ReplyCooldownSeconds = 10;

    /// <summary>1시간에 보낼 수 있는 답장 수.</summary>
    public const int ReplyPerHourLimit = 20;
}

/// <summary>알림의 성격. 트레이 앱이 아이콘/색을 고르는 데 쓴다.</summary>
public enum NotificationKind
{
    /// <summary>부모가 보낸 메시지.</summary>
    Message = 0,

    /// <summary>곧 종료된다는 경고.</summary>
    Warning = 1,

    /// <summary>종료했다는 알림.</summary>
    Terminated = 2
}

/// <summary>서비스 -> 트레이 앱.</summary>
public sealed class NotificationEnvelope
{
    public NotificationKind Kind { get; set; } = NotificationKind.Message;

    public string Title { get; set; } = "TimeBlocker";

    public string Body { get; set; } = string.Empty;

    /// <summary>답장을 받을 알림인지. 종료 알림 같은 것에는 답장 칸을 띄우지 않는다.</summary>
    public bool AllowReply { get; set; } = true;
}

/// <summary>트레이 앱 -> 서비스.</summary>
public sealed class NotifierReply
{
    public string Text { get; set; } = string.Empty;

    /// <summary>보낸 Windows 사용자 이름. 누가 보냈는지 알려주기 위함이다.</summary>
    public string UserName { get; set; } = string.Empty;
}
