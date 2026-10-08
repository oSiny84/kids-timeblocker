using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;
using TimeBlocker.Shared.Remote;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// Telegram 채팅을 관리자 콘솔처럼 쓰는 시나리오를 그대로 검증한다.
/// 파서 + 핸들러 + 정책 엔진을 함께 통과시킨다.
/// </summary>
public class RemoteCommandHandlerTests : IDisposable
{
    private readonly string _directory;
    private readonly JsonConfigurationStore _configStore;
    private readonly FixedClock _clock;
    private readonly TemporaryPermitManager _permits;
    private readonly NullEnforcementController _enforcement;
    private readonly RemoteCommandHandler _sut;

    private const string Source = "Telegram:123456789";

    public RemoteCommandHandlerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _configStore = new JsonConfigurationStore(Path.Combine(_directory, "config.json"));

        // 테스트 편의를 위해 모든 요일을 21:00~07:00 으로 통일한다.
        var config = _configStore.Current;
        foreach (var day in config.Schedule.Days)
        {
            day.Enabled = true;
            day.Start = "21:00";
            day.End = "07:00";
        }
        config.Telegram.AllowedUserIds = new List<long> { 123456789 };
        _configStore.Save(config);

        // 2026-09-22 21:20 (화요일, 차단 시간대)
        _clock = new FixedClock(new DateTime(2026, 9, 22, 21, 20, 0, DateTimeKind.Local));

        _permits = new TemporaryPermitManager(
            new JsonPermitStateStore(Path.Combine(_directory, "permits.json")),
            _clock,
            () => _configStore.Current.TemporaryPermit);

        _enforcement = new NullEnforcementController { StartedAtUtc = _clock.UtcNow.AddHours(-2) };

        var policy = new AccessPolicyEngine(
            () => _configStore.Current, new ScheduleManager(), _permits, _clock);

        _sut = new RemoteCommandHandler(
            _configStore, _permits, policy, new ScheduleManager(), _enforcement, _clock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 정리 실패 무시 */ }
    }

    private Task<string> RunAsync(string text) => _sut.ExecuteTextAsync(text, Source);

    // ------------------------------------------------------------------ 조회

    [Fact]
    public async Task Status_ShowsBlockedDuringSchedule()
    {
        var response = await RunAsync("status");

        Assert.Contains("TimeBlocker STATUS", response);
        Assert.Contains("Service     : RUNNING", response);
        Assert.Contains("2026-09-22 21:20", response);
        Assert.Contains("YouTube     : BLOCKED", response);
        Assert.Contains("Roblox      : BLOCKED", response);
        Assert.Contains("Temporary Permit:", response);
        Assert.Contains("None", response);
    }

    [Fact]
    public async Task Status_ShowsDnsProxyState()
    {
        var response = await RunAsync("status");

        Assert.Contains("DNS:", response);
        Assert.Contains("DNS Mode           : ProxyWithHostsFallback", response);
        Assert.Contains("DNS Proxy          : RUNNING", response);
        Assert.Contains("Fallback           : NOT ACTIVE", response);
        Assert.Contains("Upstream DNS       : 1.1.1.1, 8.8.8.8", response);
    }

    [Fact]
    public async Task Status_ShowsHostsFallbackWhenProxyFailed()
    {
        _enforcement.DnsStatus = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Failed,
            HostsFallbackActive = true,
            UpstreamServers = new[] { "1.1.1.1" }
        };

        var response = await RunAsync("status");

        Assert.Contains("DNS Proxy          : FAILED", response);
        Assert.Contains("Fallback           : HOSTS ACTIVE", response);
    }

    [Fact]
    public async Task ShortAlias_s_WorksLikeStatus()
    {
        Assert.Contains("TimeBlocker STATUS", await RunAsync("s"));
    }

    [Fact]
    public async Task Targets_ShowsEachTargetsMode()
    {
        var response = await RunAsync("targets");

        // 상태는 auto / block / unblock 셋 중 하나로만 표시된다.
        Assert.Contains("YouTube : auto", response);
        Assert.Contains("Roblox  : auto", response);
        Assert.DoesNotContain("DISABLED", response);
    }

    [Fact]
    public async Task Targets_IncludesShorts()
    {
        var response = await RunAsync("targets");

        Assert.Contains("Shorts  : auto", response);
    }

    [Fact]
    public async Task Status_ShowsBrowserPolicyState()
    {
        _enforcement.BrowserPolicyDescription = "chrome, edge (시크릿 차단 / DoH 끔)";

        var response = await RunAsync("status");

        // 쇼츠 차단이 실제로 적용됐는지 확인할 수 있는 유일한 창구다.
        Assert.Contains("Browser Policy:", response);
        Assert.Contains("chrome, edge", response);
    }

    [Fact]
    public async Task BlockShorts_TurnsOnBrowserPolicyByItself()
    {
        // 기본값은 꺼짐이다. 쇼츠는 브라우저 정책 없이는 전혀 막히지 않으므로
        // 대상을 직접 지정해 막으라고 하면 그것을 동의로 보고 함께 켠다.
        // 부모가 아이 PC 에서 설정파일을 고치게 만들면 안 된다.
        Assert.False(_configStore.Current.BrowserPolicy.Enabled);

        var response = await RunAsync("block shorts");

        Assert.True(_configStore.Current.BrowserPolicy.Enabled);
        Assert.Contains("Shorts  : BLOCKED", response);
    }

    [Fact]
    public async Task BlockShorts_SaysWhatElseItTurnedOn()
    {
        // PC 전체에 적용되는 변경이므로 조용히 넘어가면 안 된다.
        var response = await RunAsync("block shorts");

        Assert.Contains("함께 켰습니다", response);
        Assert.Contains("시크릿", response);
        Assert.Contains("policy off", response);
    }

    [Fact]
    public async Task AutoShorts_AlsoTurnsOnBrowserPolicy()
    {
        // 시간대에만 막는 경우도 정책이 있어야 동작한다.
        await RunAsync("auto shorts");

        Assert.True(_configStore.Current.BrowserPolicy.Enabled);
    }

    [Fact]
    public async Task UnblockShorts_DoesNotTurnOnBrowserPolicy()
    {
        // 막지 말라는 명령이 기능을 켤 이유는 없다.
        await RunAsync("unblock shorts");

        Assert.False(_configStore.Current.BrowserPolicy.Enabled);
    }

    [Fact]
    public async Task BlockAll_DoesNotTurnOnBrowserPolicy_ButSaysSo()
    {
        // 포괄 명령의 부수효과로 PC 전체 설정을 바꾸면 안 된다.
        // 대신 쇼츠가 실제로는 안 막힌다는 사실과 켜는 방법을 알려준다.
        var response = await RunAsync("block all");

        Assert.False(_configStore.Current.BrowserPolicy.Enabled);
        Assert.Contains("policy on", response);
    }

    [Fact]
    public async Task BlockShorts_WhenPolicyAlreadyOn_DoesNotRepeatTheNotice()
    {
        var config = _configStore.Current;
        config.BrowserPolicy.Enabled = true;
        _configStore.Save(config);

        var response = await RunAsync("block shorts");

        Assert.Contains("Shorts  : BLOCKED", response);
        Assert.DoesNotContain("함께 켰습니다", response);
    }

    // ------------------------------------------------------- policy 명령

    [Fact]
    public async Task Policy_Status_ShowsOffByDefault()
    {
        var response = await RunAsync("policy");

        Assert.Contains("Browser Policy : OFF", response);
        Assert.Contains("켜려면: policy on", response);
    }

    [Fact]
    public async Task PolicyOn_EnablesAndExplains()
    {
        var response = await RunAsync("policy on");

        Assert.True(_configStore.Current.BrowserPolicy.Enabled);
        Assert.Contains("OK", response);
        Assert.Contains("시크릿", response);
    }

    [Fact]
    public async Task PolicyOff_DisablesAndSaysRegistryIsRestored()
    {
        await RunAsync("policy on");

        var response = await RunAsync("policy off");

        Assert.False(_configStore.Current.BrowserPolicy.Enabled);
        Assert.Contains("되돌렸습니다", response);
    }

    [Fact]
    public async Task PolicyOn_WhenAlreadyOn_IsNotAnError()
    {
        await RunAsync("policy on");

        var response = await RunAsync("policy on");

        Assert.True(_configStore.Current.BrowserPolicy.Enabled);
        Assert.Contains("이미 켜져", response);
    }

    [Fact]
    public async Task PolicyOff_LeavesShortsModeAlone()
    {
        // 정책을 끄는 것과 "쇼츠를 막지 말라" 는 다른 얘기다.
        // 상태를 건드리면 다시 켰을 때 의도가 사라진다.
        await RunAsync("block shorts");
        await RunAsync("policy off");

        Assert.Equal(BlockMode.Blocked, _configStore.Current.Shorts.Mode);
    }

    [Fact]
    public async Task BlockShorts_DoesNotAffectYouTube()
    {
        // 쇼츠만 막는 것이 요점이다. 일반 YouTube 가 함께 막히면 기능 자체가 의미 없다.
        _clock.SetLocal(new DateTime(2026, 9, 22, 15, 0, 0, DateTimeKind.Local));

        var response = await RunAsync("block shorts");

        Assert.Contains("Shorts  : BLOCKED", response);
        Assert.Contains("YouTube : ALLOW", response);
    }

    [Fact]
    public async Task ShortsPermit_OpensShortsOnly()
    {
        var response = await RunAsync("shorts 30");

        Assert.Contains("OK", response);
        Assert.NotNull(_permits.GetEffective(BlockTarget.Shorts));
        Assert.Null(_permits.GetEffective(BlockTarget.YouTube));
    }

    [Fact]
    public async Task Ping_ReportsUptime()
    {
        var response = await RunAsync("ping");

        Assert.Contains("PONG", response);
        Assert.Contains("Service : RUNNING", response);
        Assert.Contains("Uptime  : 0d 02h 00m", response);
    }

    [Fact]
    public async Task Version_ReportsVersionAndRuntime()
    {
        var response = await RunAsync("version");

        Assert.Contains("TimeBlocker v", response);
        Assert.Contains(".NET", response);
    }

    [Fact]
    public async Task Help_ListsCommandGroups()
    {
        var response = await RunAsync("help");

        Assert.Contains("[Status]", response);
        Assert.Contains("[Temporary Allow]", response);
        Assert.Contains("[Schedule]", response);
    }

    // ------------------------------------------------------------ 일시 허용

    [Fact]
    public async Task Permit_AllowsAndReportsExpiry()
    {
        var response = await RunAsync("youtube 30");

        Assert.StartsWith("OK", response);
        Assert.Contains("YouTube allowed for 30 minutes.", response);
        Assert.Contains("Start  : 21:20", response);
        Assert.Contains("Expire : 21:50", response);

        // 즉시 DNS/방화벽에 반영되어야 한다.
        Assert.True(_enforcement.ApplyCount > 0);
    }

    [Fact]
    public async Task Permit_ThenStatus_ShowsAllow()
    {
        await RunAsync("youtube 30");

        var status = await RunAsync("status");

        Assert.Contains("YouTube     : ALLOW", status);
        Assert.Contains("Roblox      : BLOCKED", status);
        Assert.Contains("until 21:50", status);
    }

    [Fact]
    public async Task Permit_AutomaticallyExpires()
    {
        await RunAsync("youtube 30");

        _clock.SetLocal(new DateTime(2026, 9, 22, 21, 50, 0, DateTimeKind.Local));

        var status = await RunAsync("status");
        Assert.Contains("YouTube     : BLOCKED", status);
    }

    [Fact]
    public async Task Permit_OverMaxMinutes_IsRejected()
    {
        var response = await RunAsync("youtube 500");

        Assert.StartsWith("ERROR", response);
        Assert.Contains("120", response);
    }

    [Fact]
    public async Task Permit_OnUnblockedTarget_IsRejected()
    {
        await RunAsync("unblock youtube");

        var response = await RunAsync("youtube 30");

        Assert.StartsWith("ERROR", response);
        Assert.Contains("이미 항상 열려 있습니다", response);
        Assert.Contains("auto youtube", response);
    }

    [Fact]
    public async Task Permit_InvalidMinutes_ReturnsUsageNotException()
    {
        var response = await RunAsync("youtube abc");

        Assert.StartsWith("ERROR", response);
        Assert.Contains("Invalid minutes.", response);
        Assert.Contains("youtube <minutes>", response);
        Assert.Contains("youtube 30", response);
    }

    // ------------------------------------------- block / unblock / auto

    [Fact]
    public async Task Block_WithoutTarget_AppliesToEveryTarget()
    {
        await RunAsync("all 20");

        var response = await RunAsync("block");

        Assert.StartsWith("OK", response);
        Assert.Contains("진행 중이던 일시 허용도 취소했습니다", response);
        Assert.Contains("YouTube : BLOCKED", response);
        Assert.Contains("Roblox  : BLOCKED", response);
    }

    [Fact]
    public async Task Block_CancelsThatTargetsPermitOnly()
    {
        await RunAsync("youtube 30");
        await RunAsync("roblox 30");

        var response = await RunAsync("block youtube");

        Assert.Contains("YouTube : BLOCKED", response);
        Assert.Contains("Roblox  : ALLOW", response);
    }

    [Fact]
    public async Task Block_AfterAllPermit_KeepsOtherTargetAllowed()
    {
        await RunAsync("all 60");

        var response = await RunAsync("block youtube");

        Assert.Contains("YouTube : BLOCKED", response);
        Assert.Contains("Roblox  : ALLOW", response);
    }

    [Fact]
    public async Task Block_IgnoresSchedule()
    {
        // 차단 시간대가 아닌 낮 시간으로 옮긴다.
        _clock.SetLocal(new DateTime(2026, 9, 22, 15, 0, 0, DateTimeKind.Local));

        await RunAsync("block youtube");

        var status = await RunAsync("status");
        Assert.Contains("YouTube     : BLOCKED", status);
        Assert.Contains("block · 항상 막음", status);

        // 스케줄대로인 Roblox 는 낮에 열려 있어야 한다.
        Assert.Contains("Roblox      : ALLOW", status);
    }

    [Fact]
    public async Task Unblock_IgnoresSchedule()
    {
        await RunAsync("unblock youtube");

        var status = await RunAsync("status");

        Assert.Contains("YouTube     : ALLOW", status);
        Assert.Contains("unblock · 항상 열어둠", status);
        Assert.Contains("Roblox      : BLOCKED", status);

        // 스케줄을 따르지 않는 상태임을 분명히 알려야 한다.
        Assert.Contains("auto youtube", status);
    }

    [Fact]
    public async Task Auto_ReturnsToSchedule()
    {
        await RunAsync("block youtube");
        await RunAsync("auto youtube");

        Assert.Equal(BlockMode.Schedule, _configStore.Current.YouTube.Mode);

        // 차단 시간대라서 막힌다.
        Assert.Contains("YouTube     : BLOCKED", await RunAsync("status"));

        // 차단 시간대 밖으로 나가면 열린다.
        _clock.SetLocal(new DateTime(2026, 9, 22, 15, 0, 0, DateTimeKind.Local));
        Assert.Contains("YouTube     : ALLOW", await RunAsync("status"));
    }

    [Fact]
    public async Task Permit_StillWorksWhileBlocked()
    {
        // block 으로 잠가둬도 "30분만 열어줘" 는 되어야 한다.
        await RunAsync("block youtube");

        Assert.Contains("YouTube allowed for 30 minutes.", await RunAsync("youtube 30"));
        Assert.Contains("YouTube     : ALLOW", await RunAsync("status"));

        // 허용이 끝나면 다시 잠긴 상태로 돌아간다.
        _clock.SetLocal(new DateTime(2026, 9, 22, 21, 50, 0, DateTimeKind.Local));
        Assert.Contains("YouTube     : BLOCKED", await RunAsync("status"));
    }

    [Fact]
    public async Task ModeIsPersistedAcrossRestart()
    {
        await RunAsync("block youtube");
        await RunAsync("unblock roblox");

        var reopened = new JsonConfigurationStore(Path.Combine(_directory, "config.json"));

        Assert.Equal(BlockMode.Blocked, reopened.Current.YouTube.Mode);
        Assert.Equal(BlockMode.Open, reopened.Current.Roblox.Mode);
    }

    // -------------------------------------------------------------- 스케줄

    [Fact]
    public async Task ShowSchedule_ListsEveryDay()
    {
        var response = await RunAsync("schedule");

        Assert.Contains("MON 21:00-07:00", response);
        Assert.Contains("SUN 21:00-07:00", response);
    }

    [Fact]
    public async Task SetSchedule_PersistsImmediately()
    {
        var response = await RunAsync("schedule mon 22:00 07:00");

        Assert.StartsWith("OK", response);
        Assert.Contains("MON 22:00-07:00", response);

        // 설정 파일에 바로 저장되어 재시작 후에도 유지되어야 한다.
        var reopened = new JsonConfigurationStore(Path.Combine(_directory, "config.json"));
        Assert.Equal("22:00", reopened.Current.Schedule.GetDay(DayOfWeek.Monday).Start);
    }

    [Fact]
    public async Task SetSchedule_Range_AppliesToEveryDayInRange()
    {
        await RunAsync("schedule mon-thu 21:30 06:30");

        var config = _configStore.Current;
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday })
        {
            Assert.Equal("21:30", config.Schedule.GetDay(day).Start);
            Assert.Equal("06:30", config.Schedule.GetDay(day).End);
        }

        // 금요일은 그대로여야 한다.
        Assert.Equal("21:00", config.Schedule.GetDay(DayOfWeek.Friday).Start);
    }

    [Fact]
    public async Task SetSchedule_Off_DisablesThatDay()
    {
        await RunAsync("schedule tue off");

        Assert.False(_configStore.Current.Schedule.GetDay(DayOfWeek.Tuesday).Enabled);

        // 화요일 21:20 이므로 이제 허용 상태여야 한다.
        var status = await RunAsync("status");
        Assert.Contains("YouTube     : ALLOW", status);
    }

    [Fact]
    public async Task SetDefaultSchedule_AppliesToAllDays()
    {
        var response = await RunAsync("schedule default 20:00 06:00");

        Assert.Contains("Default blocking schedule updated.", response);
        Assert.Contains("20:00 ~ 06:00", response);

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            Assert.Equal("20:00", _configStore.Current.Schedule.GetDay(day).Start);
        }
    }

    [Fact]
    public async Task SetSchedule_TriggersPolicyReapply()
    {
        var before = _enforcement.ApplyCount;
        await RunAsync("schedule mon 22:00 07:00");
        Assert.True(_enforcement.ApplyCount > before);
    }

    // ------------------------------------------------- 옛 명령 호환

    [Fact]
    public async Task LegacyDisable_MeansUnblock()
    {
        var response = await RunAsync("disable youtube");

        Assert.StartsWith("OK", response);
        Assert.Equal(BlockMode.Open, _configStore.Current.YouTube.Mode);
        Assert.Contains("YouTube     : ALLOW", await RunAsync("status"));
    }

    [Fact]
    public async Task LegacyEnable_MeansAuto()
    {
        await RunAsync("unblock roblox");
        await RunAsync("enable roblox");

        Assert.Equal(BlockMode.Schedule, _configStore.Current.Roblox.Mode);
        Assert.Contains("Roblox      : BLOCKED", await RunAsync("status"));
    }

    [Fact]
    public async Task LegacyLock_MeansBlock()
    {
        await RunAsync("youtube 30");

        var response = await RunAsync("lock youtube");

        Assert.Equal(BlockMode.Blocked, _configStore.Current.YouTube.Mode);
        Assert.Contains("YouTube : BLOCKED", response);
    }

    // ---------------------------------------------------------------- 도메인

    [Fact]
    public async Task ShowDomains_ListsConfiguredDomains()
    {
        var response = await RunAsync("domains youtube");

        Assert.Contains("youtube.com", response);
        Assert.Contains("googlevideo.com", response);
    }

    [Fact]
    public async Task AddDomain_PersistsAndIsIdempotent()
    {
        var added = await RunAsync("domain add youtube music.youtube.com");
        Assert.StartsWith("OK", added);
        Assert.Contains("music.youtube.com", _configStore.Current.YouTube.Domains);

        var duplicate = await RunAsync("domain add youtube music.youtube.com");
        Assert.StartsWith("ERROR", duplicate);
        Assert.Contains("Already exists", duplicate);
    }

    [Fact]
    public async Task RemoveDomain_Works()
    {
        var response = await RunAsync("domain remove youtube youtu.be");

        Assert.StartsWith("OK", response);
        Assert.DoesNotContain("youtu.be", _configStore.Current.YouTube.Domains);
    }

    [Fact]
    public async Task RemoveDomain_NotFound_IsReported()
    {
        var response = await RunAsync("domain remove youtube nothere.example.com");

        Assert.StartsWith("ERROR", response);
        Assert.Contains("Not found", response);
    }

    [Fact]
    public async Task AddDomain_InvalidFormat_IsRejected()
    {
        var response = await RunAsync("domain add youtube http://bad");

        Assert.StartsWith("ERROR", response);
        Assert.Contains("Invalid domain", response);
    }

    // -------------------------------------------------------------- maxpermit

    [Fact]
    public async Task MaxPermit_ShowAndSet()
    {
        Assert.Contains("Max Permit : 120 minutes", await RunAsync("maxpermit"));

        var response = await RunAsync("maxpermit 90");
        Assert.Contains("Max Permit : 90 minutes", response);
        Assert.Equal(90, _configStore.Current.TemporaryPermit.MaxMinutes);

        // 새 상한이 즉시 적용되어야 한다.
        Assert.StartsWith("ERROR", await RunAsync("youtube 100"));
    }

    // ------------------------------------------------------------------ 기타

    [Fact]
    public async Task AdminList_ShowsConfiguredIds()
    {
        var response = await RunAsync("admin list");

        Assert.Contains("123456789", response);
        Assert.Contains("locally", response);
    }

    [Fact]
    public async Task Reload_RereadsConfigAndReapplies()
    {
        var before = _enforcement.ApplyCount;

        var response = await RunAsync("reload");

        Assert.StartsWith("OK", response);
        Assert.True(_enforcement.ApplyCount > before);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsHelpHintNotException()
    {
        var response = await RunAsync("셧다운해줘");

        Assert.StartsWith("ERROR", response);
        Assert.Contains("help", response);
    }

    // ------------------------------------------------- README 테스트 시나리오

    [Fact]
    public async Task FullScenario_FromReadme()
    {
        // 1. status
        Assert.Contains("YouTube     : BLOCKED", await RunAsync("status"));

        // 2. schedule mon-thu 21:00 07:00
        Assert.StartsWith("OK", await RunAsync("schedule mon-thu 21:00 07:00"));

        // 3. youtube 30
        Assert.Contains("YouTube allowed for 30 minutes.", await RunAsync("youtube 30"));

        // 4. status -> 허용 상태
        Assert.Contains("YouTube     : ALLOW", await RunAsync("status"));

        // 5. block youtube -> 일시 허용도 함께 취소된다
        Assert.Contains("진행 중이던 일시 허용도 취소했습니다", await RunAsync("block youtube"));

        // 6. status -> 다시 차단
        Assert.Contains("YouTube     : BLOCKED", await RunAsync("status"));
    }
}
