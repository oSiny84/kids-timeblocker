namespace TimeBlocker.Shared.Models;

/// <summary>
/// 차단 대상. All 은 "모든 대상"을 의미하는 논리 값이며
/// 실제 차단 구현(DNS/방화벽)은 YouTube / Roblox 에 대해서만 수행한다.
/// </summary>
public enum BlockTarget
{
    YouTube = 0,
    Roblox = 1,
    All = 100
}

public static class BlockTargets
{
    /// <summary>실제로 차단/허용을 적용하는 대상 목록.</summary>
    public static readonly BlockTarget[] Real = { BlockTarget.YouTube, BlockTarget.Roblox };

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
        BlockTarget.All => "ALL",
        _ => target.ToString()
    };
}
