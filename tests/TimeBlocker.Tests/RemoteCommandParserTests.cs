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

    // ------------------------------------------------------------------ lock

    [Fact]
    public void Lock_WithoutTarget_ClearsEverything()
    {
        var command = _sut.Parse("lock");

        Assert.Equal(RemoteCommandType.Lock, command.Type);
        Assert.Null(command.Target);
    }

    [Theory]
    [InlineData("lock youtube", BlockTarget.YouTube)]
    [InlineData("lock roblox", BlockTarget.Roblox)]
    public void Lock_WithTarget(string input, BlockTarget expected)
    {
        var command = _sut.Parse(input);

        Assert.Equal(RemoteCommandType.Lock, command.Type);
        Assert.Equal(expected, command.Target);
    }

    [Fact]
    public void LockAll_CancelsPermits()
    {
        var command = _sut.Parse("lock all");

        Assert.Equal(RemoteCommandType.Lock, command.Type);
        Assert.Null(command.Target);
    }

    // -------------------------------------------------- block / unblock

    [Theory]
    [InlineData("block youtube", RemoteCommandType.EnableTarget, BlockTarget.YouTube)]
    [InlineData("unblock youtube", RemoteCommandType.DisableTarget, BlockTarget.YouTube)]
    [InlineData("block roblox", RemoteCommandType.EnableTarget, BlockTarget.Roblox)]
    [InlineData("unblock rb", RemoteCommandType.DisableTarget, BlockTarget.Roblox)]
    [InlineData("BLOCK YouTube", RemoteCommandType.EnableTarget, BlockTarget.YouTube)]
    [InlineData("/unblock youtube", RemoteCommandType.DisableTarget, BlockTarget.YouTube)]
    public void BlockUnblock_SetsBlockingPolicy(
        string input, RemoteCommandType expectedType, BlockTarget expectedTarget)
    {
        // enable/disable 이 "유튜브를 켠다/끈다" 로 읽히는 문제 때문에 도입한 형태.
        var command = _sut.Parse(input);

        Assert.Equal(expectedType, command.Type);
        Assert.Equal(expectedTarget, command.Target);
    }

    [Theory]
    [InlineData("block all", RemoteCommandType.EnableTarget)]
    [InlineData("unblock all", RemoteCommandType.DisableTarget)]
    public void BlockUnblockAll_AppliesToEveryTarget(string input, RemoteCommandType expectedType)
    {
        // "block all" 은 예전에 lock(일시 허용 취소) 의 별칭이었으나,
        // "block youtube" 와 뜻이 어긋나서 "전부 막는다" 로 통일했다.
        var command = _sut.Parse(input);

        Assert.Equal(expectedType, command.Type);
        Assert.Equal(BlockTarget.All, command.Target);
    }

    [Theory]
    [InlineData("block youtube on", RemoteCommandType.EnableTarget)]
    [InlineData("block youtube off", RemoteCommandType.DisableTarget)]
    public void BlockWithOnOff_StillAccepted(string input, RemoteCommandType expectedType)
    {
        Assert.Equal(expectedType, _sut.Parse(input).Type);
    }

    [Fact]
    public void Block_WithoutTarget_ReturnsUsage()
    {
        var command = _sut.Parse("block");

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("block youtube", command.Error);
        Assert.Contains("lock", command.Error);
    }

    [Fact]
    public void Block_WithBadValue_IsRejected()
    {
        Assert.Equal(RemoteCommandType.Unknown, _sut.Parse("block youtube maybe").Type);
    }

    [Fact]
    public void Lock_IsStillSeparateFromBlock()
    {
        // lock 은 "일시 허용 취소" 로 block 과 다른 개념이다.
        Assert.Equal(RemoteCommandType.Lock, _sut.Parse("lock").Type);
        Assert.Equal(RemoteCommandType.Lock, _sut.Parse("lock youtube").Type);
    }

    // -------------------------------------------------------- enable/disable

    [Theory]
    [InlineData("enable youtube", RemoteCommandType.EnableTarget, BlockTarget.YouTube)]
    [InlineData("disable youtube", RemoteCommandType.DisableTarget, BlockTarget.YouTube)]
    [InlineData("enable roblox", RemoteCommandType.EnableTarget, BlockTarget.Roblox)]
    [InlineData("disable rb", RemoteCommandType.DisableTarget, BlockTarget.Roblox)]
    public void ParsesEnableDisable(string input, RemoteCommandType expectedType, BlockTarget expectedTarget)
    {
        var command = _sut.Parse(input);

        Assert.Equal(expectedType, command.Type);
        Assert.Equal(expectedTarget, command.Target);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable bogus")]
    public void InvalidEnableDisable_ReturnsUsage(string input)
    {
        Assert.Equal(RemoteCommandType.Unknown, _sut.Parse(input).Type);
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
        Assert.True(_sut.Parse("disable youtube").IsConfigChanging);
        Assert.True(_sut.Parse("maxpermit 60").IsConfigChanging);
        Assert.True(_sut.Parse("domain add youtube a.example.com").IsConfigChanging);

        Assert.False(_sut.Parse("status").IsConfigChanging);
        Assert.False(_sut.Parse("youtube 30").IsConfigChanging);
        Assert.False(_sut.Parse("lock").IsConfigChanging);
    }

    [Fact]
    public void KeepsRawTextForAuditLog()
    {
        Assert.Equal("schedule mon 21:00 07:00", _sut.Parse("schedule mon 21:00 07:00").RawText);
    }
}
