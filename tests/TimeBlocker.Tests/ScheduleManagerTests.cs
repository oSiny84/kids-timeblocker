using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;
using Xunit;

namespace TimeBlocker.Tests;

public class ScheduleManagerTests
{
    private readonly ScheduleManager _sut = new();

    /// <summary>모든 요일에 동일한 구간을 적용한 스케줄.</summary>
    private static WeeklySchedule EveryDay(string start, string end, bool enabled = true)
    {
        var schedule = new WeeklySchedule();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            schedule.Days.Add(new DaySchedule { Day = day, Enabled = enabled, Start = start, End = end });
        }
        return schedule;
    }

    // 2026-09-22 는 화요일.
    private static DateTime Tuesday(int hour, int minute) => new(2026, 9, 22, hour, minute, 0, DateTimeKind.Local);

    private static DateTime Wednesday(int hour, int minute) => new(2026, 9, 23, hour, minute, 0, DateTimeKind.Local);

    // ---------- 자정을 넘어가는 구간 21:00 ~ 07:00 ----------

    [Theory]
    [InlineData(20, 59, false)] // 차단 시작 직전
    [InlineData(21, 0, true)]   // 시작 시각은 포함
    [InlineData(23, 30, true)]
    public void CrossMidnight_EveningSide(int hour, int minute, bool expectedBlocked)
    {
        var schedule = EveryDay("21:00", "07:00");
        Assert.Equal(expectedBlocked, _sut.IsWithinBlockingWindow(schedule, Tuesday(hour, minute)));
    }

    [Theory]
    [InlineData(0, 30, true)]   // 자정 넘긴 새벽 - 어제 규칙이 살아 있어야 함
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]   // 종료 시각은 제외
    [InlineData(12, 0, false)]
    public void CrossMidnight_MorningSide(int hour, int minute, bool expectedBlocked)
    {
        var schedule = EveryDay("21:00", "07:00");
        Assert.Equal(expectedBlocked, _sut.IsWithinBlockingWindow(schedule, Wednesday(hour, minute)));
    }

    // ---------- 자정을 넘지 않는 일반 구간 ----------

    [Theory]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(14, 30, true)]
    [InlineData(17, 0, false)]  // 종료 시각 제외
    [InlineData(23, 0, false)]
    public void NormalRange(int hour, int minute, bool expectedBlocked)
    {
        var schedule = EveryDay("09:00", "17:00");
        Assert.Equal(expectedBlocked, _sut.IsWithinBlockingWindow(schedule, Tuesday(hour, minute)));
    }

    // ---------- 요일별 설정 ----------

    [Fact]
    public void DisabledDay_IsNeverBlocked()
    {
        var schedule = EveryDay("21:00", "07:00");
        schedule.GetDay(DayOfWeek.Tuesday).Enabled = false;

        // 화요일 23:00 - 화요일 규칙이 꺼져 있으므로 허용
        Assert.False(_sut.IsWithinBlockingWindow(schedule, Tuesday(23, 0)));

        // 하지만 수요일 새벽은 화요일 규칙이 꺼져 있으므로 역시 허용
        Assert.False(_sut.IsWithinBlockingWindow(schedule, Wednesday(1, 0)));
    }

    [Fact]
    public void SaturdayFree_ButFridayNightStillSpillsIn()
    {
        // 기본 스케줄: 금 23:00~08:00, 토 제한 없음
        var schedule = WeeklySchedule.CreateDefault();

        var fridayNight = new DateTime(2026, 9, 25, 23, 30, 0, DateTimeKind.Local); // 금요일
        var saturdayEarly = new DateTime(2026, 9, 26, 3, 0, 0, DateTimeKind.Local); // 토요일 새벽
        var saturdayNight = new DateTime(2026, 9, 26, 23, 0, 0, DateTimeKind.Local); // 토요일 밤

        Assert.True(_sut.IsWithinBlockingWindow(schedule, fridayNight));
        Assert.True(_sut.IsWithinBlockingWindow(schedule, saturdayEarly));  // 금요일 규칙이 넘어옴
        Assert.False(_sut.IsWithinBlockingWindow(schedule, saturdayNight)); // 토요일은 제한 없음
    }

    [Fact]
    public void StartEqualsEnd_MeansNoBlocking()
    {
        var schedule = EveryDay("21:00", "21:00");
        Assert.False(_sut.IsWithinBlockingWindow(schedule, Tuesday(21, 0)));
        Assert.False(_sut.IsWithinBlockingWindow(schedule, Tuesday(3, 0)));
    }

    [Fact]
    public void GetCurrentWindowEnd_CrossMidnight_ReturnsNextMorning()
    {
        var schedule = EveryDay("21:00", "07:00");

        var endFromEvening = _sut.GetCurrentWindowEnd(schedule, Tuesday(22, 0));
        Assert.Equal(Wednesday(7, 0), endFromEvening);

        var endFromMorning = _sut.GetCurrentWindowEnd(schedule, Wednesday(2, 0));
        Assert.Equal(Wednesday(7, 0), endFromMorning);
    }

    [Fact]
    public void GetCurrentWindowEnd_WhenNotBlocking_IsNull()
    {
        var schedule = EveryDay("21:00", "07:00");
        Assert.Null(_sut.GetCurrentWindowEnd(schedule, Tuesday(12, 0)));
    }

    [Fact]
    public void MissingDayRule_DoesNotThrow()
    {
        // 설정파일이 불완전해서 요일 규칙이 하나도 없는 경우
        var schedule = new WeeklySchedule();
        Assert.False(_sut.IsWithinBlockingWindow(schedule, Tuesday(23, 0)));
    }

    [Fact]
    public void InvalidTimeText_FallsBackToMidnight()
    {
        var schedule = EveryDay("bogus", "07:00");
        // "bogus" -> 00:00, 즉 00:00~07:00 구간이 된다.
        Assert.True(_sut.IsWithinBlockingWindow(schedule, Tuesday(3, 0)));
        Assert.False(_sut.IsWithinBlockingWindow(schedule, Tuesday(9, 0)));
    }
}
