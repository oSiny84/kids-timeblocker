namespace TimeBlocker.Shared.Core;

/// <summary>
/// 관리자(Telegram)와 PC 앞에 앉은 사용자 사이의 메시지 통로.
///
/// 서비스는 세션 0 에서 돌기 때문에 화면에 창을 직접 띄울 수 없다.
/// 실제 구현은 Service 쪽에 있고, Shared 는 이 추상화만 안다.
/// </summary>
public interface IUserMessenger
{
    /// <summary>PC 화면에 메시지를 띄운다. 메시지가 뜬 세션 수를 돌려준다.</summary>
    Task<int> SendToPcAsync(string message, CancellationToken cancellationToken = default);
}
