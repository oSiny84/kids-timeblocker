using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Service.Blocking.BrowserPolicy;
using TimeBlocker.Service.Blocking.Dns;
using TimeBlocker.Service.Maintenance;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;
using TimeBlocker.Shared.Remote;
using Xunit;

namespace TimeBlocker.Tests;

// ===================================================================== 가짜 구현

/// <summary>실패를 주입할 수 있는 가짜 hosts 관리자.</summary>
internal sealed class FakeHostsFileManager : IHostsFileManager
{
    private List<string> _domains = new();

    public bool ThrowOnClear { get; set; }

    /// <summary>true 면 Clear 를 호출해도 구간이 남아 있는 것처럼 동작한다.</summary>
    public bool ClearSilentlyFails { get; set; }

    public bool Apply(IReadOnlyCollection<string> domains)
    {
        _domains = domains.ToList();
        return true;
    }

    public bool Clear()
    {
        if (ThrowOnClear) throw new UnauthorizedAccessException("hosts denied");
        if (ClearSilentlyFails) return false;

        var had = _domains.Count > 0;
        _domains = new List<string>();
        return had;
    }

    public IReadOnlyList<string> GetManagedDomains() => _domains;
}

internal sealed class FakeFirewallManager : IFirewallManager
{
    private readonly HashSet<string> _rules = new(StringComparer.OrdinalIgnoreCase);

    public bool ThrowOnClear { get; set; }
    public bool ClearSilentlyFails { get; set; }

    public void Seed(params string[] ruleNames)
    {
        foreach (var name in ruleNames) _rules.Add(name);
    }

    public Task<bool> BlockAsync(string ruleName, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        _rules.Add(ruleName);
        return Task.FromResult(true);
    }

    public Task<bool> ClearAsync(string ruleName, CancellationToken ct)
    {
        if (ThrowOnClear) throw new InvalidOperationException("netsh failed");
        if (ClearSilentlyFails) return Task.FromResult(false);

        return Task.FromResult(_rules.Remove(ruleName));
    }

    public Task<bool> ExistsAsync(string ruleName, CancellationToken ct) =>
        Task.FromResult(_rules.Contains(ruleName));
}

internal sealed class FakeAdapterConfigurator : INetworkAdapterDnsConfigurator
{
    public bool HasSavedOriginal { get; set; }
    public IReadOnlyList<string> ConfiguredAdapterNames { get; set; } = Array.Empty<string>();

    public bool RestoreThrows { get; set; }

    /// <summary>true 면 복구해도 백업이 남는다. (일부 어댑터 복구 실패 상황)</summary>
    public bool RestoreLeavesBackup { get; set; }

    public int RestoreCallCount { get; private set; }

    public Task<AdapterDnsApplyResult> PointToLocalProxyAsync(bool configureIpv6, CancellationToken ct) =>
        Task.FromResult(new AdapterDnsApplyResult(true, 1, null));

    public Task<int> RestoreOriginalAsync(CancellationToken ct)
    {
        RestoreCallCount++;
        if (RestoreThrows) throw new InvalidOperationException("netsh restore failed");

        if (!RestoreLeavesBackup)
        {
            HasSavedOriginal = false;
            ConfiguredAdapterNames = Array.Empty<string>();
        }
        return Task.FromResult(1);
    }

    public Task<int> RecoverFromUncleanShutdownAsync(CancellationToken ct) => RestoreOriginalAsync(ct);

    public string DescribeCurrentAdapterDns() => "Wi-Fi=192.168.0.1";
}

internal sealed class FakeDnsCacheFlusher : IDnsCacheFlusher
{
    public bool Throws { get; set; }
    public int FlushCount { get; private set; }

    public Task FlushAsync(CancellationToken ct)
    {
        FlushCount++;
        if (Throws) throw new InvalidOperationException("flush failed");
        return Task.CompletedTask;
    }
}

internal sealed class FakeBrowserPolicyManager : IBrowserPolicyManager
{
    public bool HasSavedOriginal { get; set; }

    public bool RestoreThrows { get; set; }

    /// <summary>true 면 복구해도 백업이 남는다. (일부 브라우저 복구 실패 상황)</summary>
    public bool RestoreLeavesBackup { get; set; }

    public int RestoreCallCount { get; private set; }

    public bool Apply(TimeBlockerConfig config, IReadOnlyCollection<string> urlPatterns) => false;

    public bool Restore()
    {
        RestoreCallCount++;
        if (RestoreThrows) throw new InvalidOperationException("registry restore failed");

        if (!RestoreLeavesBackup) HasSavedOriginal = false;
        return true;
    }

    public string Describe(TimeBlockerConfig config) => "test";
}

// ===================================================================== 복구 테스트

/// <summary>
/// cleanup / uninstall 공통 원상복구 로직.
/// 핵심 성질: 어느 단계가 실패해도 나머지 단계는 계속 수행되어야 한다.
/// </summary>
public class SystemRestoreServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly string? _previousDataDir;

    private readonly FakeHostsFileManager _hosts = new();
    private readonly FakeFirewallManager _firewall = new();
    private readonly FakeAdapterConfigurator _adapters = new();
    private readonly FakeDnsCacheFlusher _dnsCache = new();
    private readonly FakeBrowserPolicyManager _browserPolicy = new();

    public SystemRestoreServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_directory, "state"));

        // AppPaths 가 이 임시 폴더를 보도록 한다.
        _previousDataDir = Environment.GetEnvironmentVariable("TIMEBLOCKER_DATA");
        Environment.SetEnvironmentVariable("TIMEBLOCKER_DATA", _directory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TIMEBLOCKER_DATA", _previousDataDir);
        try { Directory.Delete(_directory, recursive: true); } catch { /* 무시 */ }
    }

    private SystemRestoreService CreateSut() =>
        new(_hosts, _firewall, _adapters, _dnsCache, _browserPolicy, NullLogger.Instance);

    private Task<RestoreReport> RunAsync(bool removeState = false) =>
        CreateSut().RestoreAsync(removeState, CancellationToken.None);

    [Fact]
    public async Task CleanRun_ReportsAllStepsSucceeded()
    {
        var report = await RunAsync();

        Assert.True(report.AllSucceeded);
        Assert.Contains("System restored successfully.", report.Format());
    }

    [Fact]
    public async Task RestoresDnsBeforeTouchingAnythingElse()
    {
        _adapters.HasSavedOriginal = true;
        _adapters.ConfiguredAdapterNames = new[] { "Wi-Fi" };

        var report = await RunAsync();

        // DNS 복구는 hosts/방화벽보다 먼저 와야 한다. (인터넷을 먼저 살린다)
        var names = report.Steps.Select(s => s.Name).ToList();
        Assert.True(names.IndexOf("DNS restore") < names.IndexOf("Hosts cleanup"));
        Assert.True(names.IndexOf("DNS restore") < names.IndexOf("Firewall cleanup"));
        Assert.Equal(1, _adapters.RestoreCallCount);
    }

    [Fact]
    public async Task DnsRestoreFailure_DoesNotStopOtherSteps()
    {
        _adapters.HasSavedOriginal = true;
        _adapters.RestoreThrows = true;
        _hosts.Apply(new[] { "youtube.com" });
        _firewall.Seed(FirewallManager.RobloxRuleName);

        var report = await RunAsync();

        Assert.False(report.AllSucceeded);

        // DNS 는 실패했지만 hosts / 방화벽은 정리되어야 한다.
        Assert.False(report.Steps.Single(s => s.Name == "DNS restore").Success);
        Assert.True(report.Steps.Single(s => s.Name == "Hosts cleanup").Success);
        Assert.True(report.Steps.Single(s => s.Name == "Firewall cleanup").Success);
        Assert.Empty(_hosts.GetManagedDomains());
    }

    [Fact]
    public async Task HostsFailure_DoesNotStopFirewallCleanup()
    {
        _hosts.ThrowOnClear = true;
        _firewall.Seed(FirewallManager.RobloxRuleName);

        var report = await RunAsync();

        Assert.False(report.Steps.Single(s => s.Name == "Hosts cleanup").Success);
        Assert.True(report.Steps.Single(s => s.Name == "Firewall cleanup").Success);
        Assert.False(await _firewall.ExistsAsync(FirewallManager.RobloxRuleName, CancellationToken.None));
    }

    [Fact]
    public async Task FirewallFailure_IsReportedButOtherStepsSucceed()
    {
        _firewall.Seed(FirewallManager.RobloxRuleName);
        _firewall.ClearSilentlyFails = true;

        var report = await RunAsync();

        Assert.False(report.Steps.Single(s => s.Name == "Firewall cleanup").Success);
        Assert.True(report.Steps.Single(s => s.Name == "DNS restore").Success);
    }

    [Fact]
    public async Task PartialFailure_ReportMentionsRemedy()
    {
        _adapters.HasSavedOriginal = true;
        _adapters.RestoreLeavesBackup = true;
        _adapters.ConfiguredAdapterNames = new[] { "이더넷" };

        var report = await RunAsync();
        var text = report.Format();

        Assert.False(report.AllSucceeded);
        Assert.Contains("조치 방법:", text);
        Assert.Contains("자동으로 DNS 서버 주소 받기", text);
        Assert.Contains("partially restored", text);
    }

    [Fact]
    public async Task OnlyRemovesTimeBlockerFirewallRules()
    {
        // 사용자의 다른 방화벽 규칙은 절대 건드리면 안 된다.
        _firewall.Seed(FirewallManager.RobloxRuleName, "MyGame_Allow", "Company_VPN");

        await RunAsync();

        Assert.False(await _firewall.ExistsAsync(FirewallManager.RobloxRuleName, CancellationToken.None));
        Assert.True(await _firewall.ExistsAsync("MyGame_Allow", CancellationToken.None));
        Assert.True(await _firewall.ExistsAsync("Company_VPN", CancellationToken.None));
    }

    [Fact]
    public async Task RemovesPermitStateFile()
    {
        var permitPath = Path.Combine(_directory, "state", "permits.json");
        File.WriteAllText(permitPath, "[]");

        var report = await RunAsync();

        Assert.True(report.Steps.Single(s => s.Name == "Permit cleanup").Success);
        Assert.False(File.Exists(permitPath));
    }

    [Fact]
    public async Task StateCleanup_KeepsBackupWhenDnsNotFullyRestored()
    {
        // 어댑터 백업이 남아 있으면 상태 폴더를 지우면 안 된다. (복구 정보를 잃는다)
        _adapters.HasSavedOriginal = true;
        _adapters.RestoreLeavesBackup = true;

        var report = await RunAsync(removeState: true);
        var step = report.Steps.Single(s => s.Name == "State cleanup");

        Assert.False(step.Success);
        Assert.True(Directory.Exists(Path.Combine(_directory, "state")));
    }

    [Fact]
    public async Task StateCleanup_RemovesFolderWhenFullyRestored()
    {
        var report = await RunAsync(removeState: true);

        Assert.True(report.Steps.Single(s => s.Name == "State cleanup").Success);
        Assert.False(Directory.Exists(Path.Combine(_directory, "state")));
    }

    [Fact]
    public async Task FlushesDnsCache()
    {
        await RunAsync();
        Assert.Equal(1, _dnsCache.FlushCount);
    }

    [Fact]
    public async Task DnsCacheFailure_IsNotFatal()
    {
        _dnsCache.Throws = true;

        var report = await RunAsync();

        Assert.False(report.Steps.Single(s => s.Name == "DNS cache flush").Success);
        // 나머지 단계는 모두 성공해야 한다.
        Assert.True(report.Steps.Where(s => s.Name != "DNS cache flush").All(s => s.Success));
    }

    [Fact]
    public async Task ReportFormat_ListsEveryStep()
    {
        var text = (await RunAsync()).Format();

        Assert.Contains("DNS restore", text);
        Assert.Contains("Hosts cleanup", text);
        Assert.Contains("Firewall cleanup", text);
        Assert.Contains("Browser policy restore", text);
    }

    // ------------------------------------------------------- 브라우저 정책 복구

    [Fact]
    public async Task BrowserPolicy_SkippedWhenNothingWasChanged()
    {
        // 백업이 없으면 우리가 건드린 것도 없다. 레지스트리를 추측으로 지우면 안 된다.
        var report = await RunAsync();
        var step = report.Steps.Single(s => s.Name == "Browser policy restore");

        Assert.True(step.Success);
        Assert.Contains("SKIPPED", step.Result);
        Assert.Equal(0, _browserPolicy.RestoreCallCount);
    }

    [Fact]
    public async Task BrowserPolicy_RestoredWhenBackupExists()
    {
        _browserPolicy.HasSavedOriginal = true;

        var report = await RunAsync();

        Assert.True(report.Steps.Single(s => s.Name == "Browser policy restore").Success);
        Assert.Equal(1, _browserPolicy.RestoreCallCount);
    }

    [Fact]
    public async Task BrowserPolicyFailure_DoesNotStopOtherSteps()
    {
        _browserPolicy.HasSavedOriginal = true;
        _browserPolicy.RestoreThrows = true;

        var report = await RunAsync();

        Assert.False(report.Steps.Single(s => s.Name == "Browser policy restore").Success);
        // 레지스트리 복구가 실패해도 인터넷을 살리는 단계는 끝나 있어야 한다.
        Assert.True(report.Steps.Single(s => s.Name == "Hosts cleanup").Success);
        Assert.True(report.Steps.Single(s => s.Name == "DNS cache flush").Success);
    }

    [Fact]
    public async Task BrowserPolicy_PartialFailureIsReported()
    {
        // 일부 브라우저만 복구된 경우. 백업이 남아 있으면 실패로 봐야 한다.
        _browserPolicy.HasSavedOriginal = true;
        _browserPolicy.RestoreLeavesBackup = true;

        var report = await RunAsync();
        var step = report.Steps.Single(s => s.Name == "Browser policy restore");

        Assert.False(step.Success);
        Assert.NotNull(step.Remedy);
    }

    [Fact]
    public async Task StateCleanup_KeepsBackupWhenBrowserPolicyNotRestored()
    {
        // 브라우저 정책 백업이 남아 있으면 상태 폴더를 지우면 안 된다.
        // 지우면 원래 레지스트리 값으로 되돌릴 근거가 사라진다.
        _browserPolicy.HasSavedOriginal = true;
        _browserPolicy.RestoreLeavesBackup = true;

        var report = await RunAsync(removeState: true);
        var step = report.Steps.Single(s => s.Name == "State cleanup");

        Assert.False(step.Success);
        Assert.True(Directory.Exists(Path.Combine(_directory, "state")));
    }
}

// ===================================================================== doctor 리포트

public class DoctorReportTests
{
    [Fact]
    public void AllPass_OverallIsPass()
    {
        var report = new DoctorReport()
            .Add(DoctorCheck.Pass("A"))
            .Add(DoctorCheck.Pass("B", "detail"));

        Assert.Equal(DoctorStatus.Pass, report.Overall);
        Assert.Contains("Result : PASS", report.Format());
    }

    [Fact]
    public void WarnOnly_OverallIsWarn()
    {
        var report = new DoctorReport()
            .Add(DoctorCheck.Pass("A"))
            .Add(DoctorCheck.Warn("B", "없음", "설치하세요"));

        Assert.Equal(DoctorStatus.Warn, report.Overall);

        var text = report.Format();
        Assert.Contains("Result : WARN (1 warning)", text);
        Assert.Contains("[WARN] B : 없음", text);
        Assert.Contains("- B: 설치하세요", text);
    }

    [Fact]
    public void AnyFail_OverallIsFail()
    {
        var report = new DoctorReport()
            .Add(DoctorCheck.Pass("A"))
            .Add(DoctorCheck.Warn("B"))
            .Add(DoctorCheck.Fail("C", "죽음", "다시 시작하세요"));

        Assert.Equal(DoctorStatus.Fail, report.Overall);
        Assert.Contains("Result : FAIL (1 warning, 1 failure)", report.Format());
    }

    [Fact]
    public void PassItems_DoNotAppearInRemedySection()
    {
        var report = new DoctorReport()
            .Add(DoctorCheck.Pass("Healthy"))
            .Add(DoctorCheck.Fail("Broken", "x", "고치세요"));

        var text = report.Format();
        var remedyPart = text[text.IndexOf("조치 방법:", StringComparison.Ordinal)..];

        Assert.DoesNotContain("Healthy", remedyPart);
        Assert.Contains("Broken", remedyPart);
    }

    [Fact]
    public void NoRemedies_OmitsRemedySection()
    {
        var text = new DoctorReport().Add(DoctorCheck.Pass("A")).Format();
        Assert.DoesNotContain("조치 방법:", text);
    }

    [Fact]
    public void Format_StartsWithTitle()
    {
        Assert.StartsWith("TimeBlocker Doctor", new DoctorReport().Add(DoctorCheck.Pass("A")).Format());
    }
}

// ===================================================================== doctor 명령

public class DoctorCommandParsingTests
{
    private readonly RemoteCommandParser _sut = new();

    [Theory]
    [InlineData("doctor")]
    [InlineData("/doctor")]
    [InlineData("DOCTOR")]
    [InlineData("diag")]
    [InlineData("healthcheck")]
    public void ParsesDoctorCommand(string input)
    {
        Assert.Equal(RemoteCommandType.Doctor, _sut.Parse(input).Type);
    }

    [Fact]
    public void Doctor_IsNotConfigChanging()
    {
        // 읽기 전용 점검이다. 설정을 바꾸지 않는다.
        Assert.False(_sut.Parse("doctor").IsConfigChanging);
    }

    [Fact]
    public void HelpIncludesDoctor()
    {
        Assert.Contains("doctor", ResponseFormatter.HelpText);
    }
}

/// <summary>doctor 명령이 진단 서비스를 호출하는지.</summary>
public class DoctorCommandHandlerTests : IDisposable
{
    private readonly string _directory;
    private readonly FakeDiagnostics _diagnostics = new();
    private readonly RemoteCommandHandler _sut;

    private sealed class FakeDiagnostics : IDiagnosticsService
    {
        public int CallCount { get; private set; }

        public Task<DoctorReport> RunAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new DoctorReport().Add(DoctorCheck.Pass("Administrator", "SYSTEM")));
        }
    }

    public DoctorCommandHandlerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var configStore = new Shared.Configuration.JsonConfigurationStore(Path.Combine(_directory, "config.json"));
        var clock = new Shared.Common.FixedClock(new DateTime(2026, 9, 22, 21, 20, 0, DateTimeKind.Local));
        var permits = new Shared.Core.TemporaryPermitManager(
            new Shared.Core.InMemoryPermitStateStore(), clock, () => configStore.Current.TemporaryPermit);
        var policy = new Shared.Core.AccessPolicyEngine(
            () => configStore.Current, new Shared.Core.ScheduleManager(), permits, clock);

        _sut = new RemoteCommandHandler(
            configStore, permits, policy, new Shared.Core.ScheduleManager(),
            new Shared.Core.NullEnforcementController(), clock,
            parser: null, logger: null, diagnostics: _diagnostics);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 무시 */ }
    }

    [Fact]
    public async Task DoctorCommand_InvokesDiagnostics()
    {
        var response = await _sut.ExecuteTextAsync("doctor", "test");

        Assert.Equal(1, _diagnostics.CallCount);
        Assert.Contains("TimeBlocker Doctor", response);
        Assert.Contains("[PASS] Administrator", response);
    }

    [Fact]
    public async Task WithoutDiagnostics_ReturnsClearError()
    {
        var configStore = new Shared.Configuration.JsonConfigurationStore(Path.Combine(_directory, "c2.json"));
        var clock = new Shared.Common.FixedClock(DateTime.Now);
        var permits = new Shared.Core.TemporaryPermitManager(
            new Shared.Core.InMemoryPermitStateStore(), clock, () => configStore.Current.TemporaryPermit);
        var policy = new Shared.Core.AccessPolicyEngine(
            () => configStore.Current, new Shared.Core.ScheduleManager(), permits, clock);

        var handler = new RemoteCommandHandler(
            configStore, permits, policy, new Shared.Core.ScheduleManager(),
            new Shared.Core.NullEnforcementController(), clock);

        var response = await handler.ExecuteTextAsync("doctor", "test");

        Assert.StartsWith("ERROR", response);
    }
}
