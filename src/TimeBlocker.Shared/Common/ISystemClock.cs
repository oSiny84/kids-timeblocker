namespace TimeBlocker.Shared.Common;

/// <summary>
/// 시간 의존 로직을 단위 테스트할 수 있게 하기 위한 얇은 추상화.
/// 스케줄 판정은 LocalNow, 일시 허용 만료는 UtcNow 를 사용한다.
/// </summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }

    DateTime LocalNow { get; }
}

public sealed class SystemClock : ISystemClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTime LocalNow => DateTime.Now;
}

/// <summary>테스트용 고정 시계.</summary>
public sealed class FixedClock : ISystemClock
{
    public FixedClock(DateTime localNow)
    {
        SetLocal(localNow);
    }

    public DateTimeOffset UtcNow { get; private set; }

    public DateTime LocalNow { get; private set; }

    public void SetLocal(DateTime localNow)
    {
        LocalNow = DateTime.SpecifyKind(localNow, DateTimeKind.Local);
        UtcNow = new DateTimeOffset(LocalNow);
    }

    public void Advance(TimeSpan delta) => SetLocal(LocalNow + delta);
}
