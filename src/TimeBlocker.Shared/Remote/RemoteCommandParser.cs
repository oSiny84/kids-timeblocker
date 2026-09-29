using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Remote;

public interface IRemoteCommandParser
{
    RemoteCommand Parse(string? text);
}

/// <summary>
/// Telegram 채팅 / 로컬 CLI 입력을 RemoteCommand 로 변환한다.
/// 파싱만 담당하고 상태는 건드리지 않는다. (실행은 RemoteCommandHandler)
///
/// 슬래시(/)는 있어도 되고 없어도 된다. 대소문자를 구분하지 않는다.
/// 그룹 채팅의 "/status@MyBot" 형태도 처리한다.
/// </summary>
public sealed class RemoteCommandParser : IRemoteCommandParser
{
    /// <summary>maxpermit 으로 설정할 수 있는 상한/하한.</summary>
    public const int MaxPermitCeiling = 720;
    public const int MaxPermitFloor = 1;

    public RemoteCommand Parse(string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0) return RemoteCommand.Invalid(raw, "ERROR\nEmpty command.\n\nType 'help' for usage.");

        var body = raw.StartsWith('/') ? raw[1..] : raw;
        var tokens = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return RemoteCommand.Invalid(raw, "ERROR\nEmpty command.\n\nType 'help' for usage.");

        // "/status@TimeBlockerBot" -> "status"
        var head = tokens[0];
        var atIndex = head.IndexOf('@');
        if (atIndex > 0) head = head[..atIndex];
        head = head.ToLowerInvariant();

        var args = tokens.Skip(1).ToArray();

        return head switch
        {
            "status" or "s" => Simple(RemoteCommandType.Status, raw),
            "targets" or "target" => Simple(RemoteCommandType.Targets, raw),
            "ping" => Simple(RemoteCommandType.Ping, raw),
            "version" or "ver" => Simple(RemoteCommandType.Version, raw),
            "reload" => Simple(RemoteCommandType.Reload, raw),
            // 명령 목록을 찾을 때 사람마다 떠올리는 단어가 다르다. 전부 help 로 받는다.
            "help" or "h" or "start" or "?" or "list" or "commands" or "command" or "cmd" or "menu"
                => Simple(RemoteCommandType.Help, raw),
            "doctor" or "diag" or "healthcheck" => Simple(RemoteCommandType.Doctor, raw),

            "schedule" or "sch" => ParseSchedule(raw, args),
            "lock" => ParseLock(raw, args),
            "block" => ParseBlock(raw, args, block: true),
            "unblock" => ParseBlock(raw, args, block: false),
            "enable" => ParseEnableDisable(raw, args, enable: true),
            "disable" => ParseEnableDisable(raw, args, enable: false),
            "domains" or "domain" => ParseDomain(raw, head, args),
            "maxpermit" or "max" => ParseMaxPermit(raw, args),
            "admin" => ParseAdmin(raw, args),
            "dns" => ParseDns(raw, args),

            _ => ParsePermitOrUnknown(raw, head, args)
        };
    }

    private static RemoteCommand Simple(RemoteCommandType type, string raw) =>
        new() { Type = type, RawText = raw };

    // ---------------------------------------------------------------- permit

    /// <summary>"youtube 30", "yt 30", "all 20" 형태.</summary>
    private static RemoteCommand ParsePermitOrUnknown(string raw, string head, string[] args)
    {
        if (!TryParseTarget(head, out var target))
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nUnknown command: {head}\n\nType 'help' for usage.");
        }

        var usage = $"Usage:\n{head} <minutes>\n\nExample:\n{head} 30";

        if (args.Length == 0)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nMinutes required.\n\n{usage}");
        }

        if (!int.TryParse(args[0], out var minutes))
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nInvalid minutes.\n\n{usage}");
        }

        if (minutes <= 0)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nMinutes must be 1 or more.\n\n{usage}");
        }

        return new RemoteCommand
        {
            Type = RemoteCommandType.Permit,
            Target = target,
            Minutes = minutes,
            RawText = raw
        };
    }

    // ------------------------------------------------------------------ lock

    /// <summary>"lock", "lock youtube".</summary>
    private static RemoteCommand ParseLock(string raw, string[] args)
    {
        if (args.Length == 0)
        {
            return new RemoteCommand { Type = RemoteCommandType.Lock, Target = null, RawText = raw };
        }

        if (!TryParseTarget(args[0], out var target))
        {
            return RemoteCommand.Invalid(raw,
                $"ERROR\nUnknown target: {args[0]}\n\nUsage:\nlock\nlock youtube\nlock roblox");
        }

        // "lock all" 은 전체 취소와 같다.
        return new RemoteCommand
        {
            Type = RemoteCommandType.Lock,
            Target = target == BlockTarget.All ? null : target,
            RawText = raw
        };
    }

    /// <summary>
    /// block / unblock 계열 명령. 읽는 그대로의 뜻이다.
    ///
    ///   block youtube      YouTube 를 막는다      (= enable youtube)
    ///   unblock youtube    YouTube 를 안 막는다   (= disable youtube)
    ///   block all          모든 대상을 막는다
    ///   unblock all        모든 대상을 안 막는다
    ///
    /// enable/disable 은 "유튜브를 켠다/끈다" 로 읽혀 뜻이 정반대로 이해되기 쉽다.
    /// on/off 를 덧붙인 형태(block youtube on)도 계속 받는다.
    ///
    /// 주의: 일시 허용을 취소하는 것은 lock 이다. block 과는 다른 개념이다.
    ///   block youtube   = 차단 정책을 켠다 (스케줄이 맞으면 막힌다)
    ///   lock youtube    = 지금 걸려 있는 일시 허용만 취소한다
    /// </summary>
    private static RemoteCommand ParseBlock(string raw, string[] args, bool block)
    {
        var verb = block ? "block" : "unblock";
        var usage =
            $"Usage:\n{verb} youtube\n{verb} roblox\n{verb} all\n\n" +
            "일시 허용만 취소하려면: lock / lock youtube";

        if (args.Length == 0)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\n대상을 지정하세요.\n\n{usage}");
        }

        if (!TryParseTarget(args[0], out var target))
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nUnknown target: {args[0]}\n\n{usage}");
        }

        // "block youtube on" / "block youtube off" 형태도 계속 받아준다.
        var enable = block;
        if (args.Length >= 2)
        {
            switch (args[1].ToLowerInvariant())
            {
                case "on": enable = true; break;
                case "off": enable = false; break;
                default:
                    return RemoteCommand.Invalid(raw,
                        $"ERROR\n알 수 없는 값입니다: {args[1]}\n\n{usage}");
            }
        }

        return new RemoteCommand
        {
            Type = enable ? RemoteCommandType.EnableTarget : RemoteCommandType.DisableTarget,
            Target = target,
            RawText = raw
        };
    }

    // ---------------------------------------------------------- enable/disable

    private static RemoteCommand ParseEnableDisable(string raw, string[] args, bool enable)
    {
        var verb = enable ? "enable" : "disable";
        var usage = $"Usage:\n{verb} youtube\n{verb} roblox\n{verb} all";

        if (args.Length == 0)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nTarget required.\n\n{usage}");
        }

        // all 도 받는다. block/unblock 과 동작을 맞추기 위함이다.
        if (!TryParseTarget(args[0], out var target))
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nUnknown target: {args[0]}\n\n{usage}");
        }

        return new RemoteCommand
        {
            Type = enable ? RemoteCommandType.EnableTarget : RemoteCommandType.DisableTarget,
            Target = target,
            RawText = raw
        };
    }

    // ---------------------------------------------------------------- domains

    /// <summary>"domains youtube", "domain add youtube music.youtube.com".</summary>
    private static RemoteCommand ParseDomain(string raw, string head, string[] args)
    {
        const string usage =
            "Usage:\ndomains youtube\ndomain add youtube music.youtube.com\ndomain remove youtube music.youtube.com";

        if (args.Length == 0)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nTarget required.\n\n{usage}");
        }

        var first = args[0].ToLowerInvariant();

        if (first is "add" or "remove" or "del" or "delete")
        {
            if (args.Length < 3)
            {
                return RemoteCommand.Invalid(raw, $"ERROR\nTarget and domain required.\n\n{usage}");
            }

            if (!TryParseTarget(args[1], out var domainTarget) || domainTarget == BlockTarget.All)
            {
                return RemoteCommand.Invalid(raw, $"ERROR\nUnknown target: {args[1]}\n\n{usage}");
            }

            var domain = args[2].Trim().ToLowerInvariant();
            if (!IsValidDomain(domain))
            {
                return RemoteCommand.Invalid(raw, $"ERROR\nInvalid domain: {args[2]}\n\n{usage}");
            }

            return new RemoteCommand
            {
                Type = first == "add" ? RemoteCommandType.AddDomain : RemoteCommandType.RemoveDomain,
                Target = domainTarget,
                Domain = domain,
                RawText = raw
            };
        }

        if (!TryParseTarget(first, out var showTarget) || showTarget == BlockTarget.All)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nUnknown target: {args[0]}\n\n{usage}");
        }

        return new RemoteCommand { Type = RemoteCommandType.ShowDomains, Target = showTarget, RawText = raw };
    }

    /// <summary>
    /// 도메인 형식 검사. 와일드카드/프로토콜/경로는 거부한다.
    /// 예) music.youtube.com 은 통과, http://a.com, a b, -a.com 은 거부.
    /// </summary>
    public static bool IsValidDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        if (domain.Length > 253) return false;
        if (domain.Contains('/') || domain.Contains(':') || domain.Contains(' ')) return false;

        var labels = domain.Split('.');
        if (labels.Length < 2) return false;

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63) return false;
            if (label.StartsWith('-') || label.EndsWith('-')) return false;

            foreach (var c in label)
            {
                var ok = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-';
                if (!ok) return false;
            }
        }

        // 최상위 라벨은 숫자로만 이루어질 수 없다. (IP 주소 배제)
        return !labels[^1].All(char.IsDigit);
    }

    // -------------------------------------------------------------- maxpermit

    private static RemoteCommand ParseMaxPermit(string raw, string[] args)
    {
        if (args.Length == 0)
        {
            return new RemoteCommand { Type = RemoteCommandType.ShowMaxPermit, RawText = raw };
        }

        var usage = $"Usage:\nmaxpermit\nmaxpermit 120\n\nRange: {MaxPermitFloor} ~ {MaxPermitCeiling} minutes";

        if (!int.TryParse(args[0], out var value))
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nInvalid minutes.\n\n{usage}");
        }

        if (value < MaxPermitFloor || value > MaxPermitCeiling)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nOut of range.\n\n{usage}");
        }

        return new RemoteCommand { Type = RemoteCommandType.SetMaxPermit, Value = value, RawText = raw };
    }

    // ------------------------------------------------------------------ admin

    private static RemoteCommand ParseAdmin(string raw, string[] args)
    {
        if (args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteCommand { Type = RemoteCommandType.AdminList, RawText = raw };
        }

        // 보안상 Telegram 으로 관리자 추가/삭제는 지원하지 않는다. (설치 시 로컬에서만 설정)
        return RemoteCommand.Invalid(raw,
            "ERROR\nOnly 'admin list' is supported.\nAdmin IDs can only be changed locally on the PC.");
    }

    // ------------------------------------------------------------------- dns

    /// <summary>
    /// "dns status"  현재 DNS 동작 상태
    /// "dns test"    지금 즉시 self-test 수행
    /// "dns restore" 어댑터 DNS 를 원래 설정으로 즉시 복구
    /// </summary>
    private static RemoteCommand ParseDns(string raw, string[] args)
    {
        const string usage = "Usage:\ndns status\ndns test\ndns restore";

        if (args.Length == 0)
        {
            // 인자 없이 "dns" 만 보내면 상태 조회로 본다.
            return new RemoteCommand { Type = RemoteCommandType.DnsStatus, RawText = raw };
        }

        return args[0].ToLowerInvariant() switch
        {
            "status" or "s" => new RemoteCommand { Type = RemoteCommandType.DnsStatus, RawText = raw },
            "test" or "selftest" => new RemoteCommand { Type = RemoteCommandType.DnsTest, RawText = raw },
            "restore" or "rollback" => new RemoteCommand { Type = RemoteCommandType.DnsRestore, RawText = raw },
            _ => RemoteCommand.Invalid(raw, $"ERROR\nUnknown dns command: {args[0]}\n\n{usage}")
        };
    }

    // --------------------------------------------------------------- schedule

    /// <summary>
    /// "schedule"                      현재 설정 조회
    /// "schedule mon 21:00 07:00"      요일 지정
    /// "schedule mon-thu 21:00 07:00"  요일 범위
    /// "schedule weekday 21:00 07:00"  요일 그룹
    /// "schedule sat off"              해당 요일 제한 없음
    /// "schedule default 21:00 07:00"  전체 기본값
    /// </summary>
    private static RemoteCommand ParseSchedule(string raw, string[] args)
    {
        const string usage =
            "Usage:\nschedule\nschedule mon 21:00 07:00\nschedule mon-thu 21:00 07:00\n" +
            "schedule weekday 21:00 07:00\nschedule sat off\nschedule default 21:00 07:00";

        if (args.Length == 0)
        {
            return new RemoteCommand { Type = RemoteCommandType.ShowSchedule, RawText = raw };
        }

        var daySpec = args[0].ToLowerInvariant();
        var isDefault = daySpec is "default" or "all" or "everyday";

        if (!isDefault && !TryParseDays(daySpec, out _))
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nUnknown day: {args[0]}\n\n{usage}");
        }

        TryParseDays(daySpec, out var days);
        if (isDefault) days = Enum.GetValues<DayOfWeek>().ToList();

        if (args.Length < 2)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nTime required.\n\n{usage}");
        }

        // "schedule sat off"
        if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (isDefault)
            {
                return RemoteCommand.Invalid(raw, $"ERROR\n'default off' is not supported.\n\n{usage}");
            }

            return new RemoteCommand
            {
                Type = RemoteCommandType.SetSchedule,
                Days = days,
                TurnOff = true,
                RawText = raw
            };
        }

        if (args.Length < 3)
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nStart and end time required.\n\n{usage}");
        }

        if (!TryParseTime(args[1], out var start) || !TryParseTime(args[2], out var end))
        {
            return RemoteCommand.Invalid(raw, $"ERROR\nInvalid time format. Use HH:mm.\n\n{usage}");
        }

        if (start == end)
        {
            return RemoteCommand.Invalid(raw,
                $"ERROR\nStart and end must differ. Use 'off' for no restriction.\n\n{usage}");
        }

        return new RemoteCommand
        {
            Type = isDefault ? RemoteCommandType.SetDefaultSchedule : RemoteCommandType.SetSchedule,
            Days = days,
            Start = start,
            End = end,
            RawText = raw
        };
    }

    /// <summary>"21:00", "2100", "9:05" 를 TimeSpan 으로.</summary>
    public static bool TryParseTime(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        int hour, minute;

        if (trimmed.Contains(':'))
        {
            var parts = trimmed.Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute)) return false;
        }
        else if (trimmed.Length == 4 && trimmed.All(char.IsDigit))
        {
            hour = int.Parse(trimmed[..2]);
            minute = int.Parse(trimmed[2..]);
        }
        else
        {
            return false;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59) return false;

        value = new TimeSpan(hour, minute, 0);
        return true;
    }

    /// <summary>"mon", "mon-thu", "weekday", "weekend", "all" 을 요일 목록으로.</summary>
    public static bool TryParseDays(string? spec, out List<DayOfWeek> days)
    {
        days = new List<DayOfWeek>();
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var text = spec.Trim().ToLowerInvariant();

        switch (text)
        {
            case "weekday" or "weekdays":
                days.AddRange(new[]
                {
                    DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                    DayOfWeek.Thursday, DayOfWeek.Friday
                });
                return true;

            case "weekend" or "weekends":
                days.AddRange(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });
                return true;

            case "all" or "everyday" or "daily" or "default":
                days.AddRange(Enum.GetValues<DayOfWeek>());
                return true;
        }

        // "mon-thu" 범위. 월요일 기준 순환으로 계산한다. (일요일이 0 이면 mon-thu 범위 계산이 어색해짐)
        if (text.Contains('-'))
        {
            var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            if (!TryParseSingleDay(parts[0], out var from) || !TryParseSingleDay(parts[1], out var to)) return false;

            var fromIndex = MondayIndex(from);
            var toIndex = MondayIndex(to);
            var count = ((toIndex - fromIndex) + 7) % 7;

            for (var i = 0; i <= count; i++)
            {
                days.Add(FromMondayIndex((fromIndex + i) % 7));
            }
            return true;
        }

        // "mon,tue" 쉼표 목록
        if (text.Contains(','))
        {
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseSingleDay(part, out var day)) return false;
                if (!days.Contains(day)) days.Add(day);
            }
            return days.Count > 0;
        }

        if (!TryParseSingleDay(text, out var single)) return false;
        days.Add(single);
        return true;
    }

    private static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static DayOfWeek FromMondayIndex(int index) => (DayOfWeek)((index + 1) % 7);

    public static bool TryParseSingleDay(string? text, out DayOfWeek day)
    {
        day = DayOfWeek.Monday;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "mon" or "monday" or "월": day = DayOfWeek.Monday; return true;
            case "tue" or "tues" or "tuesday" or "화": day = DayOfWeek.Tuesday; return true;
            case "wed" or "wednesday" or "수": day = DayOfWeek.Wednesday; return true;
            case "thu" or "thur" or "thurs" or "thursday" or "목": day = DayOfWeek.Thursday; return true;
            case "fri" or "friday" or "금": day = DayOfWeek.Friday; return true;
            case "sat" or "saturday" or "토": day = DayOfWeek.Saturday; return true;
            case "sun" or "sunday" or "일": day = DayOfWeek.Sunday; return true;
            default: return false;
        }
    }

    private static bool TryParseTarget(string? text, out BlockTarget target)
    {
        target = BlockTarget.All;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "youtube" or "yt" or "y":
                target = BlockTarget.YouTube;
                return true;
            case "roblox" or "rb" or "rbx" or "r":
                target = BlockTarget.Roblox;
                return true;
            case "all" or "everything":
                target = BlockTarget.All;
                return true;
            default:
                return false;
        }
    }
}
