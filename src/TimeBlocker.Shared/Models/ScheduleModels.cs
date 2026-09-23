using System.Text.Json.Serialization;

namespace TimeBlocker.Shared.Models;

/// <summary>
/// 하루 단위의 차단 시간 규칙.
/// Start 가 End 보다 크면 자정을 넘어가는 구간으로 해석한다. (예: 21:00 ~ 07:00)
/// 규칙은 "Start 시각이 속한 요일"에 소속된다. 즉 월요일 21:00~07:00 규칙은
/// 화요일 새벽 00:30 까지를 포함한다.
/// </summary>
public sealed class DaySchedule
{
    public DayOfWeek Day { get; set; }

    /// <summary>false 이면 그 요일은 제한 없음.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>"HH:mm" 형식.</summary>
    public string Start { get; set; } = "21:00";

    /// <summary>"HH:mm" 형식.</summary>
    public string End { get; set; } = "07:00";

    [JsonIgnore]
    public TimeSpan StartTime => TimeUtil.ParseHhMm(Start);

    [JsonIgnore]
    public TimeSpan EndTime => TimeUtil.ParseHhMm(End);

    /// <summary>자정을 넘어가는 구간인지.</summary>
    [JsonIgnore]
    public bool CrossesMidnight => StartTime > EndTime;

    public DaySchedule Clone() => new()
    {
        Day = Day,
        Enabled = Enabled,
        Start = Start,
        End = End
    };

    public override string ToString() => Enabled ? $"{Start} ~ {End}" : "제한 없음";
}

/// <summary>요일 7개 규칙의 묶음.</summary>
public sealed class WeeklySchedule
{
    public List<DaySchedule> Days { get; set; } = new();

    /// <summary>기본값: 월~목/일 21:00~07:00, 금 23:00~08:00, 토 제한 없음.</summary>
    public static WeeklySchedule CreateDefault()
    {
        var schedule = new WeeklySchedule();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            var rule = new DaySchedule { Day = day, Enabled = true, Start = "21:00", End = "07:00" };
            switch (day)
            {
                case DayOfWeek.Friday:
                    rule.Start = "23:00";
                    rule.End = "08:00";
                    break;
                case DayOfWeek.Saturday:
                    rule.Enabled = false;
                    break;
            }
            schedule.Days.Add(rule);
        }
        return schedule;
    }

    public DaySchedule GetDay(DayOfWeek day)
    {
        var found = Days.FirstOrDefault(d => d.Day == day);
        if (found is not null) return found;

        // 설정파일이 불완전해도 죽지 않도록 비활성 규칙을 만들어 반환한다.
        var placeholder = new DaySchedule { Day = day, Enabled = false };
        Days.Add(placeholder);
        return placeholder;
    }

    public WeeklySchedule Clone() => new() { Days = Days.Select(d => d.Clone()).ToList() };
}

public static class TimeUtil
{
    /// <summary>"HH:mm" 문자열을 TimeSpan 으로. 잘못된 값은 00:00 으로 처리한다.</summary>
    public static TimeSpan ParseHhMm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;

        var parts = text.Trim().Split(':');
        if (parts.Length < 2) return TimeSpan.Zero;
        if (!int.TryParse(parts[0], out var hour)) return TimeSpan.Zero;
        if (!int.TryParse(parts[1], out var minute)) return TimeSpan.Zero;

        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        return new TimeSpan(hour, minute, 0);
    }

    public static string ToHhMm(TimeSpan value) => $"{value.Hours:D2}:{value.Minutes:D2}";
}
