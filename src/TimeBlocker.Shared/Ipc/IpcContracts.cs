namespace TimeBlocker.Shared.Ipc;

/// <summary>로컬 관리자 CLI 가 서비스에 보낼 수 있는 요청 종류.</summary>
public enum IpcCommand
{
    /// <summary>서비스 생존 확인.</summary>
    Ping,

    /// <summary>
    /// Telegram 과 동일한 텍스트 명령을 실행한다.
    /// 예: "status", "youtube 30", "schedule mon 21:00 07:00"
    /// </summary>
    ExecuteText,

    /// <summary>최근 로그 조회.</summary>
    GetLogTail
}

/// <summary>Named Pipe 로 오가는 요청. 한 줄 JSON.</summary>
public sealed class IpcRequest
{
    public IpcCommand Command { get; set; } = IpcCommand.Ping;

    /// <summary>ExecuteText 용 명령 문자열.</summary>
    public string? Text { get; set; }

    /// <summary>GetLogTail 용: 가져올 줄 수.</summary>
    public int LineCount { get; set; } = 200;

    /// <summary>호출자 식별 문자열. 감사 로그에 남는다. (예: "CLI:DOMAIN\\user")</summary>
    public string? Source { get; set; }
}

public sealed class IpcResponse
{
    public bool Success { get; set; }

    /// <summary>사람이 읽는 응답 본문. CLI 는 이것을 그대로 출력한다.</summary>
    public string Message { get; set; } = string.Empty;

    public List<string>? LogLines { get; set; }

    public static IpcResponse Ok(string message = "OK") => new() { Success = true, Message = message };

    public static IpcResponse Fail(string message) => new() { Success = false, Message = message };
}
