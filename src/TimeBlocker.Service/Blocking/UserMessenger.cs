using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Core;

namespace TimeBlocker.Service.Blocking;

/// <summary>
/// Telegram 에서 온 "msg ..." 를 PC 화면에 띄운다.
/// 누가 보냈는지 분명히 하기 위해 제목을 고정한다.
/// </summary>
public sealed class UserMessenger : IUserMessenger
{
    private readonly IUserSessionNotifier _notifier;
    private readonly ILogger<UserMessenger> _logger;

    public UserMessenger(IUserSessionNotifier notifier, ILogger<UserMessenger> logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    public Task<int> SendToPcAsync(string message, CancellationToken cancellationToken = default)
    {
        var shown = _notifier.Notify("부모님 메시지", message);

        // 본문은 남기지 않는다. 언제 몇 개 띄웠는지만 기록한다.
        _logger.LogInformation("PC 화면 메시지를 표시했습니다. (창 {Count}개, {Length}자)", shown, message.Length);

        return Task.FromResult(shown);
    }
}
