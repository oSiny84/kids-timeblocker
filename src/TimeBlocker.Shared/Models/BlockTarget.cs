namespace TimeBlocker.Shared.Models;

/// <summary>
/// 차단 대상. All 은 "모든 대상"을 의미하는 논리 값이며
/// 실제 차단은 YouTube / Roblox / Shorts 에 대해 수행한다.
///
/// 대상마다 쓰는 수단이 다르다.
///   YouTube / Roblox - DNS(도메인) + 방화벽 + 프로세스 종료
///   Shorts           - 브라우저 정책(URL 경로). DNS 로는 경로를 구분할 수 없다.
/// </summary>
public enum BlockTarget
{
    YouTube = 0,
    Roblox = 1,

    /// <summary>YouTube Shorts 만. 일반 YouTube 영상은 건드리지 않는다.</summary>
    Shorts = 2,

    All = 100
}

public static class BlockTargets
{
    /// <summary>
    /// 실제로 차단/허용을 적용하는 대상 목록.
    /// 상태 표시 순서를 겸하므로 YouTube 바로 뒤에 Shorts 를 둔다.
    /// </summary>
    public static readonly BlockTarget[] Real =
        { BlockTarget.YouTube, BlockTarget.Shorts, BlockTarget.Roblox };

    public static bool TryParse(string? text, out BlockTarget target)
    {
        target = BlockTarget.All;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "youtube":
            case "yt":
                target = BlockTarget.YouTube;
                return true;
            case "roblox":
            case "rbx":
                target = BlockTarget.Roblox;
                return true;
            case "shorts":
            case "short":
            case "sh":
            case "쇼츠":
                target = BlockTarget.Shorts;
                return true;
            case "all":
            case "전체":
                target = BlockTarget.All;
                return true;
            default:
                return false;
        }
    }

    public static string ToDisplayName(this BlockTarget target) => target switch
    {
        BlockTarget.YouTube => "YouTube",
        BlockTarget.Roblox => "Roblox",
        BlockTarget.Shorts => "Shorts",
        BlockTarget.All => "ALL",
        _ => target.ToString()
    };
}
