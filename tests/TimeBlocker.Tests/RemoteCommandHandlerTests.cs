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
    public async Task Targets_ShowsBlockingOnOff()
    {
        var response = await RunAsync("targets");

        // "ENABLED/DISABLED" 는 "유튜브를 켠다/끈다" 로 오해하기 쉬워
        // "차단 ON/OFF" 로 표시한다.
        Assert.Contains("YouTube : 차단 ON", response);
        Assert.Contains("Roblox  : 차단 ON", response);
        Assert.DoesNotContain("DISABLED", response);
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
    public async Task Permit_OnDisabledTarget_IsRejected()
    {
        await RunAsync("disable youtube");

        var response = await RunAsync("youtube 30");

        Assert.StartsWith("ERROR", response);
        Assert.Contains("차단이 꺼져 있어", response);
        Assert.Contains("block youtube on", response);
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

    // ------------------------------------------------------------------ lock

    [Fact]
    public async Task Lock_CancelsAllPermits()
    {
        await RunAsync("all 20");

        var response = await RunAsync("lock");

        Assert.StartsWith("OK", response);
        Assert.Contains("All temporary permits cancelled.", response);
        Assert.Contains("YouTube : BLOCKED", response);
        Assert.Contains("Roblox  : BLOCKED", response);
    }

    [Fact]
    public async Task LockTarget_CancelsOnlyThatTarget()
    {
        await RunAsync("youtube 30");
        await RunAsync("roblox 30");

        var response = await RunAsync("lock youtube");

        Assert.Contains("YouTube temporary permit cancelled.", response);
        Assert.Contains("YouTube : BLOCKED", response);
        Assert.Contains("Roblox  : ALLOW", response);
    }

    [Fact]
    public async Task LockTarget_AfterAllPermit_KeepsOtherTargetAllowed()
    {
        await RunAsync("all 60");

        var response = await RunAsync("lock youtube");

        Assert.Contains("YouTube : BLOCKED", response);
        Assert.Contains("Roblox  : ALLOW", response);
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

    // ------------------------------------------------------- 대상 ON/OFF

    [Fact]
    public async Task DisableTarget_RemovesItFromBlocking()
    {
        var response = await RunAsync("disable youtube");

        Assert.StartsWith("OK", response);
        Assert.Contains("차단을 껐습니다", response);
        Assert.Contains("YouTube : 차단 OFF", response);
        Assert.False(_configStore.Current.YouTube.Enabled);

        var status = await RunAsync("status");
        Assert.Contains("YouTube     : OFF (차단 안 함)", status);
        Assert.Contains("Roblox      : BLOCKED", status);

        // 왜 안 막히는지 status 안에서 바로 알 수 있어야 한다.
        Assert.Contains("block youtube on", status);
    }

    [Fact]
    public async Task EnableTarget_RestoresBlocking()
    {
        await RunAsync("disable roblox");
        await RunAsync("enable roblox");

        Assert.True(_configStore.Current.Roblox.Enabled);
        Assert.Contains("Roblox      : BLOCKED", await RunAsync("status"));
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

        // 5. lock youtube
        Assert.Contains("YouTube temporary permit cancelled.", await RunAsync("lock youtube"));

        // 6. status -> 다시 차단
        Assert.Contains("YouTube     : BLOCKED", await RunAsync("status"));
    }
}
