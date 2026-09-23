namespace TimeBlocker.Service.Remote;

/// <summary>
/// 원격 명령 채널 추상화.
/// 1차 구현은 Telegram 이지만, 나중에 Discord 등을 붙일 수 있도록 분리한다.
///
/// 중요: 이 채널이 죽어도 로컬 차단 기능은 계속 동작해야 한다.
/// 따라서 제공자는 자체적으로 재시도하고, 실패를 밖으로 전파하지 않는다.
/// </summary>
public interface IRemoteCommandProvider
{
    /// <summary>표시용 이름. 예: "Telegram"</summary>
    string Name { get; }

    /// <summary>현재 정상 동작 중인지.</summary>
    bool IsRunning { get; }

    /// <summary>마지막 상태/오류 요약. 민감정보를 담지 않는다.</summary>
    string? StatusText { get; }

    /// <summary>수신 루프를 시작한다. 취소될 때까지 돌아간다.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
