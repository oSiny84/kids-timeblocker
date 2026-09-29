using System.Text;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Remote;

/// <summary>
/// Telegram / CLI 응답 문자열을 만든다.
/// 메시지가 지나치게 길어지지 않도록 필요한 정보만 담는다.
/// </summary>
public static class ResponseFormatter
{
    private static readonly string[] ShortDayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    /// <summary>월요일부터 일요일 순서. 사람이 읽는 순서에 맞춘다.</summary>
    private static readonly DayOfWeek[] DisplayOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    /// <summary>
    /// 같은 설정이 연속된 요일을 묶어서 표시한다.
    ///   Mon-Thu 21:00 ~ 07:00
    ///   Fri     23:00 ~ 08:00
    ///   Sat     OFF
    /// </summary>
    public static string FormatSchedule(WeeklySchedule schedule)
    {
        var builder = new StringBuilder();

        var index = 0;
        while (index < DisplayOrder.Length)
        {
            var first = schedule.GetDay(DisplayOrder[index]);
            var last = index;

            // 같은 설정이 이어지는 동안 묶는다.
            while (last + 1 < DisplayOrder.Length && SameRule(first, schedule.GetDay(DisplayOrder[last + 1])))
            {
                last++;
            }

            var label = index == last
                ? ShortDayNames[(int)DisplayOrder[index]]
                : $"{ShortDayNames[(int)DisplayOrder[index]]}-{ShortDayNames[(int)DisplayOrder[last]]}";

            var value = first.Enabled && first.StartTime != first.EndTime
                ? $"{first.Start} ~ {first.End}"
                : "OFF";

            builder.AppendLine($"{label,-7} {value}");
            index = last + 1;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>요일별 한 줄씩. "schedule" 명령 응답용.</summary>
    public static string FormatScheduleDetailed(WeeklySchedule schedule)
    {
        var builder = new StringBuilder();
        foreach (var day in DisplayOrder)
        {
            var rule = schedule.GetDay(day);
            var value = rule.Enabled && rule.StartTime != rule.EndTime ? $"{rule.Start}-{rule.End}" : "OFF";
            builder.AppendLine($"{ShortDayNames[(int)day].ToUpperInvariant()} {value}");
        }
        return builder.ToString().TrimEnd();
    }

    private static bool SameRule(DaySchedule a, DaySchedule b)
    {
        var aOff = !a.Enabled || a.StartTime == a.EndTime;
        var bOff = !b.Enabled || b.StartTime == b.EndTime;
        if (aOff || bOff) return aOff && bOff;
        return a.Start == b.Start && a.End == b.End;
    }

    /// <summary>"2d 04h 21m" 형태.</summary>
    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        return $"{uptime.Days}d {uptime.Hours:D2}h {uptime.Minutes:D2}m";
    }

    /// <summary>남은 시간을 분 단위로. 1분 미만은 "&lt;1".</summary>
    public static string FormatRemaining(DateTimeOffset expiresUtc, DateTimeOffset nowUtc)
    {
        var remaining = expiresUtc - nowUtc;
        if (remaining <= TimeSpan.Zero) return "0";

        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        return minutes <= 0 ? "<1" : minutes.ToString();
    }

    /// <summary>일시 허용 목록. 없으면 "None".</summary>
    public static string FormatPermits(IReadOnlyList<TemporaryPermit> permits, DateTimeOffset nowUtc)
    {
        if (permits.Count == 0) return "None";

        var builder = new StringBuilder();
        foreach (var permit in permits.OrderBy(p => p.ExpireTimeUtc))
        {
            var expires = permit.ExpireTimeUtc.ToLocalTime();
            builder.AppendLine(
                $"{permit.Target.ToDisplayName(),-8}until {expires:HH:mm} ({FormatRemaining(permit.ExpireTimeUtc, nowUtc)} min left)");
        }
        return builder.ToString().TrimEnd();
    }

    public static string BlockedOrAllowed(bool isBlocked) => isBlocked ? "BLOCKED" : "ALLOW";

    public static string HelpText =>
        """
        TimeBlocker Commands

        [Status]
        status (s)
        targets
        schedule (sch)

        [State] 대상마다 아래 셋 중 하나
        block youtube     계속 막는다 (스케줄 무시)
        unblock youtube   계속 열어둔다 (스케줄 무시)
        auto youtube      스케줄대로 (기본값)

        대상은 youtube / roblox / all.
        대상을 빼면 전체에 적용된다. (block = block all)

        [Temporary Allow] 지금만 잠깐 열어주기
        youtube <min>   (yt)
        roblox <min>    (rb)
        all <min>

        시간이 지나면 원래 상태로 돌아간다.
        block 상태여도 일시 허용은 먹는다.

        [Schedule]
        schedule mon 21:00 07:00
        schedule mon-thu 21:00 07:00
        schedule weekday 21:00 07:00
        schedule sat off
        schedule default 21:00 07:00

        [Domains]
        domains youtube
        domain add youtube music.youtube.com
        domain remove youtube music.youtube.com

        [Config]
        maxpermit
        maxpermit 120
        reload

        [DNS]
        dns status
        dns test
        dns restore

        [System]
        ping
        version
        doctor
        help  (list / menu / ?)
        """;
}
