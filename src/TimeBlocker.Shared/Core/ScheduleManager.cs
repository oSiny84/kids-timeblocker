using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Core;

public interface IScheduleManager
{
    /// <summary>주어진 로컬 시각이 차단 시간대인지.</summary>
    bool IsWithinBlockingWindow(WeeklySchedule schedule, DateTime localNow);

    /// <summary>현재(또는 다음) 적용 구간을 사람이 읽을 수 있는 문자열로.</summary>
    string Describe(WeeklySchedule schedule, DateTime localNow);

    /// <summary>현재 차단 구간이 끝나는 로컬 시각. 차단 중이 아니면 null.</summary>
    DateTime? GetCurrentWindowEnd(WeeklySchedule schedule, DateTime localNow);
}

/// <summary>
/// 요일별 차단 시간 판정.
///
/// 규칙은 "시작 시각이 속한 요일"에 소속된다.
/// 따라서 21:00~07:00 같이 자정을 넘는 구간을 판정하려면
///   (1) 오늘 규칙의 저녁 부분   : now >= Start
///   (2) 어제 규칙의 새벽 부분   : now <  End
/// 두 가지를 모두 확인해야 한다.
///
/// 경계 처리: Start 는 포함(inclusive), End 는 제외(exclusive).
///   21:00 ~ 07:00 이면 21:00 => BLOCK, 07:00 => ALLOW.
/// Start == End 인 규칙은 "구간 길이 0" 으로 보고 차단하지 않는다.
/// </summary>
public sealed class ScheduleManager : IScheduleManager
{
    public bool IsWithinBlockingWindow(WeeklySchedule schedule, DateTime localNow)
    {
        if (schedule is null) return false;

        var timeOfDay = localNow.TimeOfDay;

        // (1) 오늘 시작하는 규칙
        var today = schedule.GetDay(localNow.DayOfWeek);
        if (IsEffective(today))
        {
            if (today.CrossesMidnight)
            {
                if (timeOfDay >= today.StartTime) return true;
            }
            else if (timeOfDay >= today.StartTime && timeOfDay < today.EndTime)
            {
                return true;
            }
        }

        // (2) 어제 시작해서 자정을 넘어온 규칙
        var yesterdayDay = localNow.AddDays(-1).DayOfWeek;
        var yesterday = schedule.GetDay(yesterdayDay);
        if (IsEffective(yesterday) && yesterday.CrossesMidnight && timeOfDay < yesterday.EndTime)
        {
            return true;
        }

        return false;
    }

    public DateTime? GetCurrentWindowEnd(WeeklySchedule schedule, DateTime localNow)
    {
        if (schedule is null) return null;

        var timeOfDay = localNow.TimeOfDay;
        var midnight = localNow.Date;

        var today = schedule.GetDay(localNow.DayOfWeek);
        if (IsEffective(today))
        {
            if (today.CrossesMidnight && timeOfDay >= today.StartTime)
            {
                // 오늘 저녁 시작 -> 내일 새벽에 끝남
                return midnight.AddDays(1) + today.EndTime;
            }
            if (!today.CrossesMidnight && timeOfDay >= today.StartTime && timeOfDay < today.EndTime)
            {
                return midnight + today.EndTime;
            }
        }

        var yesterday = schedule.GetDay(localNow.AddDays(-1).DayOfWeek);
        if (IsEffective(yesterday) && yesterday.CrossesMidnight && timeOfDay < yesterday.EndTime)
        {
            return midnight + yesterday.EndTime;
        }

        return null;
    }

    public string Describe(WeeklySchedule schedule, DateTime localNow)
    {
        if (schedule is null) return "스케줄 없음";

        var today = schedule.GetDay(localNow.DayOfWeek);
        return IsEffective(today)
            ? $"{today.Start} ~ {today.End}"
            : "오늘 제한 없음";
    }

    /// <summary>실제로 차단 효과가 있는 규칙인지. (꺼져 있거나 길이가 0이면 무효)</summary>
    private static bool IsEffective(DaySchedule day) =>
        day is { Enabled: true } && day.StartTime != day.EndTime;
}
