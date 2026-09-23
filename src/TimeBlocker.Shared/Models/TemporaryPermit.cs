using System.Text.Json.Serialization;

namespace TimeBlocker.Shared.Models;

/// <summary>
/// 일시 허용 정보. 만료 시각은 항상 UTC 절대시각으로 저장한다.
/// (재부팅/서비스 재시작 후에도 정확히 복원하기 위함)
/// </summary>
public sealed class TemporaryPermit
{
    public BlockTarget Target { get; set; }

    public DateTimeOffset StartTimeUtc { get; set; }

    public DateTimeOffset ExpireTimeUtc { get; set; }

    /// <summary>누가 요청했는지. "GUI", "Telegram:123456789" 등.</summary>
    public string Source { get; set; } = "unknown";

    [JsonIgnore]
    public TimeSpan Duration => ExpireTimeUtc - StartTimeUtc;

    public bool IsActiveAt(DateTimeOffset utcNow) => utcNow < ExpireTimeUtc;

    /// <summary>이 허용이 지정한 대상에 적용되는가. All 허용은 모든 대상에 적용된다.</summary>
    public bool Covers(BlockTarget target) => Target == BlockTarget.All || Target == target;

    public TemporaryPermit Clone() => new()
    {
        Target = Target,
        StartTimeUtc = StartTimeUtc,
        ExpireTimeUtc = ExpireTimeUtc,
        Source = Source
    };
}
