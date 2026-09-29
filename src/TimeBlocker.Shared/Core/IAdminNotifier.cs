namespace TimeBlocker.Shared.Core;

/// <summary>
/// 관리자(Telegram)에게 먼저 말을 거는 통로.
/// 명령에 대한 응답이 아니라, PC 쪽에서 생긴 일을 알릴 때 쓴다.
/// </summary>
public interface IAdminNotifier
{
    /// <summary>등록된 관리자 전원에게 보낸다. 보낸 사람 수를 돌려준다.</summary>
    Task<int> NotifyAdminsAsync(string message, CancellationToken cancellationToken = default);
}
