using TimeBlocker.Shared.Models;
using TimeBlocker.Shared.Remote;
using Xunit;

namespace TimeBlocker.Tests;

public class RemoteCommandParserTests
{
    private readonly RemoteCommandParser _sut = new();

    // ------------------------------------------------------------ 일시 허용

    [Theory]
    [InlineData("youtube 30", BlockTarget.YouTube, 30)]
    [InlineData("yt 30", BlockTarget.YouTube, 30)]
    [InlineData("/youtube 30", BlockTarget.YouTube, 30)]
    [InlineData("roblox 60", BlockTarget.Roblox, 60)]
    [InlineData("rb 60", BlockTarget.Roblox, 60)]
    [InlineData("all 20", BlockTarget.All, 20)]
    [InlineData("shorts 15", BlockTarget.Shorts, 15)]
    [InlineData("sh 15", BlockTarget.Shorts, 15)]
    [InlineData("쇼츠 15", BlockTarget.Shorts, 15)]
    [InlineData("  YouTube   45  ", BlockTarget.YouTube, 45)]
    public void ParsesPermitCommands(string input, BlockTarget expectedTarget, int expectedMinutes)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Permit, command.Type);
        Assert.Equal(expectedTarget, command.Target);
        Assert.Equal(expectedMinutes, command.Minutes);
    }

    [Theory]
    [InlineData("youtube abc")]
    [InlineData("youtube")]
    [InlineData("youtube 0")]
    [InlineData("youtube -5")]
    public void InvalidPermit_ReturnsUsage(string input)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.StartsWith("ERROR", command.Error);
        Assert.Contains("Usage:", command.Error);
    }

    [Fact]
    public void OverLimitMinutes_ParsesFine_LimitEnforcedByHandler()
    {
        // 파서는 형식만 검사한다. 최대 허용시간은 PermitManager 가 판단한다.
        var command = _sut.Parse("youtube 500");

        Assert.Equal(RemoteCommandType.Permit, command.Type);
        Assert.Equal(500, command.Minutes);
    }

    // ----------------------------------------------------------- 단순 명령

    [Theory]
    [InlineData("status", RemoteCommandType.Status)]
    [InlineData("s", RemoteCommandType.Status)]
    [InlineData("/status", RemoteCommandType.Status)]
    [InlineData("status@TimeBlockerBot", RemoteCommandType.Status)]
    [InlineData("targets", RemoteCommandType.Targets)]
    [InlineData("ping", RemoteCommandType.Ping)]
    [InlineData("version", RemoteCommandType.Version)]
    [InlineData("reload", RemoteCommandType.Reload)]
    [InlineData("help", RemoteCommandType.Help)]
    [InlineData("list", RemoteCommandType.Help)]
    [InlineData("commands", RemoteCommandType.Help)]
    [InlineData("menu", RemoteCommandType.Help)]
    [InlineData("?", RemoteCommandType.Help)]
    [InlineData("schedule", RemoteCommandType.ShowSchedule)]
    [InlineData("sch", RemoteCommandType.ShowSchedule)]
    [InlineData("maxpermit", RemoteCommandType.ShowMaxPermit)]
    [InlineData("admin list", RemoteCommandType.AdminList)]
    public void ParsesSimpleCommands(string input, RemoteCommandType expected)
    {
        Assert.Equal(expected, _sut.Parse(input).Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("bogus")]
    [InlineData("bogus 10")]
    public void UnknownInput_IsRejectedWithoutThrowing(string? input)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.False(string.IsNullOrWhiteSpace(command.Error));
    }

    // -------------------------------------------- block / unblock / auto

    [Theory]
    [InlineData("block youtube", BlockMode.Blocked, BlockTarget.YouTube)]
    [InlineData("unblock youtube", BlockMode.Open, BlockTarget.YouTube)]
    [InlineData("auto youtube", BlockMode.Schedule, BlockTarget.YouTube)]
    [InlineData("block roblox", BlockMode.Blocked, BlockTarget.Roblox)]
    [InlineData("unblock rb", BlockMode.Open, BlockTarget.Roblox)]
    [InlineData("auto rb", BlockMode.Schedule, BlockTarget.Roblox)]
    [InlineData("BLOCK YouTube", BlockMode.Blocked, BlockTarget.YouTube)]
    [InlineData("/unblock youtube", BlockMode.Open, BlockTarget.YouTube)]
    [InlineData("block all", BlockMode.Blocked, BlockTarget.All)]
    [InlineData("auto all", BlockMode.Schedule, BlockTarget.All)]
    [InlineData("block shorts", BlockMode.Blocked, BlockTarget.Shorts)]
    [InlineData("unblock sh", BlockMode.Open, BlockTarget.Shorts)]
    [InlineData("auto shorts", BlockMode.Schedule, BlockTarget.Shorts)]
    public void SetMode_ParsesEveryState(string input, BlockMode expectedMode, BlockTarget expectedTarget)
    {
        // 상태는 셋 중 하나로만 정해진다. 어떤 명령을 쳐도 애매하게 남지 않는다.
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.SetMode, command.Type);
        Assert.Equal(expectedMode, command.Mode);
        Assert.Equal(expectedTarget, command.Target);
    }

    [Theory]
    [InlineData("block", BlockMode.Blocked)]
    [InlineData("unblock", BlockMode.Open)]
    [InlineData("auto", BlockMode.Schedule)]
    public void SetMode_WithoutTarget_MeansAll(string input, BlockMode expectedMode)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.SetMode, command.Type);
        Assert.Equal(BlockTarget.All, command.Target);
        Assert.Equal(expectedMode, command.Mode);
    }

    [Theory]
    [InlineData("lock youtube", BlockMode.Blocked)]
    [InlineData("unlock youtube", BlockMode.Open)]
    [InlineData("enable youtube", BlockMode.Schedule)]
    [InlineData("disable youtube", BlockMode.Open)]
    [InlineData("block youtube on", BlockMode.Blocked)]
    [InlineData("block youtube off", BlockMode.Open)]
    public void SetMode_LegacyAliasesStillWork(string input, BlockMode expectedMode)
    {
        // 1.1 이하를 쓰던 사람이 옛 명령을 쳐도 뜻이 통해야 한다.
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.SetMode, command.Type);
        Assert.Equal(expectedMode, command.Mode);
    }

    [Theory]
    [InlineData("block bogus")]
    [InlineData("unblock bogus")]
    [InlineData("auto bogus")]
    [InlineData("block youtube maybe")]
    public void SetMode_InvalidInput_ReturnsUsage(string input)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("Usage:", command.Error);
    }

    // --------------------------------------------------------------- 메시지

    [Theory]
    [InlineData("msg 밥 먹고 하자")]
    [InlineData("say 밥 먹고 하자")]
    [InlineData("/msg 밥 먹고 하자")]
    public void SendMessage_KeepsWholeTextAfterCommand(string input)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.SendMessage, command.Type);
        Assert.Equal("밥 먹고 하자", command.Text);
    }

    [Fact]
    public void SendMessage_PreservesInnerSpacing()
    {
        // 토큰으로 잘라 붙이면 띄어쓰기가 뭉개진다.
        var command = _sut.Parse("msg 30분   뒤에   저녁");

        Assert.Equal("30분   뒤에   저녁", command.Text);
    }

    [Theory]
    [InlineData("msg")]
    [InlineData("msg    ")]
    public void SendMessage_WithoutText_ReturnsUsage(string input)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("Usage:", command.Error);
    }

    [Fact]
    public void SendMessage_TooLong_IsRejected()
    {
        var command = _sut.Parse("msg " + new string('가', 501));

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("500자", command.Error);
    }

    [Fact]
    public void SendMessage_IsNotConfigChanging()
    {
        Assert.False(_sut.Parse("msg 안녕").IsConfigChanging);
    }

    // --------------------------------------------------------------- 스케줄

    [Fact]
    public void Schedule_SingleDay()
    {
        var command = _sut.Parse("schedule mon 21:00 07:00");

        Assert.Equal(RemoteCommandType.SetSchedule, command.Type);
        Assert.Equal(new[] { DayOfWeek.Monday }, command.Days);
        Assert.Equal(new TimeSpan(21, 0, 0), command.Start);
        Assert.Equal(new TimeSpan(7, 0, 0), command.End);
        Assert.False(command.TurnOff);
    }

    [Fact]
    public void Schedule_DayRange()
    {
        var command = _sut.Parse("schedule mon-thu 21:00 07:00");

        Assert.Equal(RemoteCommandType.SetSchedule, command.Type);
        Assert.Equal(
            new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday },
            command.Days);
    }

    [Fact]
    public void Schedule_RangeWrappingWeek()
    {
        // 금 -> 월 (금,토,일,월)
        var command = _sut.Parse("schedule fri-mon 23:00 08:00");

        Assert.Equal(
            new[] { DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday },
            command.Days);
    }

    [Fact]
    public void Schedule_Weekday()
    {
        var command = _sut.Parse("schedule weekday 21:00 07:00");
        Assert.Equal(5, command.Days.Count);
        Assert.DoesNotContain(DayOfWeek.Saturday, command.Days);
        Assert.DoesNotContain(DayOfWeek.Sunday, command.Days);
    }

    [Fact]
    public void Schedule_Weekend()
    {
        var command = _sut.Parse("schedule weekend 22:00 09:00");
        Assert.Equal(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }, command.Days);
    }

    [Fact]
    public void Schedule_Off()
    {
        var command = _sut.Parse("schedule sat off");

        Assert.Equal(RemoteCommandType.SetSchedule, command.Type);
        Assert.True(command.TurnOff);
        Assert.Equal(new[] { DayOfWeek.Saturday }, command.Days);
    }

    [Fact]
    public void Schedule_Default_AppliesToAllDays()
    {
        var command = _sut.Parse("schedule default 21:00 07:00");

        Assert.Equal(RemoteCommandType.SetDefaultSchedule, command.Type);
        Assert.Equal(7, command.Days.Count);
    }

    [Theory]
    [InlineData("schedule bogus 21:00 07:00")]
    [InlineData("schedule mon")]
    [InlineData("schedule mon 21:00")]
    [InlineData("schedule mon 25:00 07:00")]
    [InlineData("schedule mon 21:70 07:00")]
    [InlineData("schedule mon abc def")]
    [InlineData("schedule mon 21:00 21:00")]  // 길이 0 구간
    [InlineData("schedule default off")]
    public void Schedule_InvalidInput_ReturnsUsage(string input)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("Usage:", command.Error);
    }

    [Fact]
    public void Schedule_AcceptsCompactTimeFormat()
    {
        var command = _sut.Parse("schedule mon 2100 0700");

        Assert.Equal(RemoteCommandType.SetSchedule, command.Type);
        Assert.Equal(new TimeSpan(21, 0, 0), command.Start);
        Assert.Equal(new TimeSpan(7, 0, 0), command.End);
    }

    // ---------------------------------------------------------------- 도메인

    [Fact]
    public void Domains_Show()
    {
        var command = _sut.Parse("domains youtube");

        Assert.Equal(RemoteCommandType.ShowDomains, command.Type);
        Assert.Equal(BlockTarget.YouTube, command.Target);
    }

    [Fact]
    public void Domain_Add()
    {
        var command = _sut.Parse("domain add youtube music.youtube.com");

        Assert.Equal(RemoteCommandType.AddDomain, command.Type);
        Assert.Equal(BlockTarget.YouTube, command.Target);
        Assert.Equal("music.youtube.com", command.Domain);
    }

    [Theory]
    [InlineData("domains shorts")]
    [InlineData("domain add shorts youtube.com")]
    [InlineData("domain remove shorts youtube.com")]
    public void Domain_RejectsShorts(string input)
    {
        // 쇼츠는 URL 경로로 막는다. 도메인을 받아주면 설정에 들어가고도 아무 효과가 없어서
        // "넣었는데 안 막힌다" 가 된다. 아예 거부하고 이유를 알려준다.
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Invalid, command.Type);
        Assert.Contains("BlockedUrlPatterns", command.Error);
    }

    [Fact]
    public void Domain_Remove()
    {
        var command = _sut.Parse("domain remove roblox web.roblox.com");

        Assert.Equal(RemoteCommandType.RemoveDomain, command.Type);
        Assert.Equal(BlockTarget.Roblox, command.Target);
        Assert.Equal("web.roblox.com", command.Domain);
    }

    [Theory]
    [InlineData("http://music.youtube.com")]
    [InlineData("music youtube com")]
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("localhost")]
    [InlineData("1.2.3.4")]
    [InlineData("a..com")]
    public void Domain_InvalidFormat_IsRejected(string domain)
    {
        var command = _sut.Parse($"domain add youtube {domain}");
        Assert.Equal(RemoteCommandType.Unknown, command.Type);
    }

    [Theory]
    [InlineData("music.youtube.com")]
    [InlineData("a.b.c.example.com")]
    [InlineData("youtu.be")]
    [InlineData("xn--80ak6aa92e.com")]
    public void Domain_ValidFormat_IsAccepted(string domain)
    {
        Assert.True(RemoteCommandParser.IsValidDomain(domain));
    }

    // -------------------------------------------------------------- maxpermit

    [Fact]
    public void MaxPermit_Set()
    {
        var command = _sut.Parse("maxpermit 90");

        Assert.Equal(RemoteCommandType.SetMaxPermit, command.Type);
        Assert.Equal(90, command.Value);
    }

    [Theory]
    [InlineData("maxpermit 0")]
    [InlineData("maxpermit 721")]
    [InlineData("maxpermit abc")]
    [InlineData("maxpermit -5")]
    public void MaxPermit_OutOfRange_IsRejected(string input)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("720", command.Error);
    }

    // ------------------------------------------------------------------ 기타

    [Fact]
    public void Admin_AddIsNotSupported()
    {
        var command = _sut.Parse("admin add 12345");

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("locally", command.Error);
    }

    [Fact]
    public void ConfigChangingFlag_IsSetOnlyForMutatingCommands()
    {
        Assert.True(_sut.Parse("schedule mon 21:00 07:00").IsConfigChanging);
        Assert.True(_sut.Parse("unblock youtube").IsConfigChanging);
        Assert.True(_sut.Parse("auto youtube").IsConfigChanging);
        Assert.True(_sut.Parse("maxpermit 60").IsConfigChanging);
        Assert.True(_sut.Parse("domain add youtube a.example.com").IsConfigChanging);

        Assert.False(_sut.Parse("status").IsConfigChanging);
        Assert.False(_sut.Parse("youtube 30").IsConfigChanging);
        Assert.False(_sut.Parse("help").IsConfigChanging);
    }

    [Fact]
    public void KeepsRawTextForAuditLog()
    {
        Assert.Equal("schedule mon 21:00 07:00", _sut.Parse("schedule mon 21:00 07:00").RawText);
    }
}
