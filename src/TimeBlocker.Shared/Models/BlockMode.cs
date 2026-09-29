namespace TimeBlocker.Shared.Models;

/// <summary>
/// 대상 하나의 상태. 항상 셋 중 정확히 하나다.
///
/// 예전에는 Enabled(true/false) 한 개로 표현했는데,
/// "차단 기능을 껐다" 와 "지금 막혀 있다" 가 뒤섞여 읽혀서 혼동이 잦았다.
/// 축을 하나로 두고 상태를 셋으로 나눈다.
/// </summary>
public enum BlockMode
{
    /// <summary>기본값. 주간 스케줄에 따라 차단 시간대에만 막는다.</summary>
    Schedule = 0,

    /// <summary>스케줄과 무관하게 계속 막는다. (block)</summary>
    Blocked = 1,

    /// <summary>스케줄과 무관하게 계속 열어둔다. (unblock)</summary>
    Open = 2
}

public static class BlockModes
{
    /// <summary>"auto" / "block" / "unblock" 같은 입력을 상태로 바꾼다.</summary>
    public static bool TryParse(string? text, out BlockMode mode)
    {
        mode = BlockMode.Schedule;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "auto":
            case "schedule":
            case "sch":
                mode = BlockMode.Schedule;
                return true;
            case "block":
            case "blocked":
            case "lock":
                mode = BlockMode.Blocked;
                return true;
            case "unblock":
            case "open":
            case "unlock":
                mode = BlockMode.Open;
                return true;
            default:
                return false;
        }
    }

    /// <summary>상태 한 줄 표기. 사람이 읽는 화면용.</summary>
    public static string ToDisplayName(this BlockMode mode) => mode switch
    {
        BlockMode.Schedule => "자동 (스케줄대로)",
        BlockMode.Blocked => "잠금 (항상 막음)",
        BlockMode.Open => "열림 (항상 열어둠)",
        _ => mode.ToString()
    };

    /// <summary>상태를 되돌리는 명령어. 안내 문구에 쓴다.</summary>
    public static string ToCommandName(this BlockMode mode) => mode switch
    {
        BlockMode.Schedule => "auto",
        BlockMode.Blocked => "block",
        BlockMode.Open => "unblock",
        _ => mode.ToString().ToLowerInvariant()
    };
}
