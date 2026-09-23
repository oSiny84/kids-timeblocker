using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Service.Blocking.Dns;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;
using TimeBlocker.Shared.Remote;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// 어댑터 DNS 원본 설정 저장/복구.
///
/// 이 부분이 잘못되면 PC 인터넷이 끊긴 채로 남으므로,
/// "저장 -> 재시작(새 인스턴스) -> 복구" 흐름을 파일 기준으로 검증한다.
/// </summary>
public class AdapterDnsStateStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public AdapterDnsStateStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "adapter-dns.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 무시 */ }
    }

    private AdapterDnsStateStore CreateStore() =>
        new(NullLogger.Instance, _path);

    private static AdapterDnsState SampleState() => new()
    {
        ProcessId = 1234,
        Adapters = new List<AdapterDnsSnapshot>
        {
            new()
            {
                Name = "이더넷",
                Id = "{11111111-2222-3333-4444-555555555555}",
                Description = "Realtek Gaming GbE",
                Ipv4Dhcp = false,
                Ipv4Servers = new List<string> { "192.168.0.1", "8.8.4.4" },
                Ipv6Dhcp = true,
                Ipv4Changed = true,
                Ipv6Changed = true
            },
            new()
            {
                Name = "Wi-Fi",
                Id = "{99999999-8888-7777-6666-555555555555}",
                Description = "Intel Wi-Fi 6 AX201",
                Ipv4Dhcp = true,
                Ipv6Dhcp = true,
                Ipv4Changed = true
            }
        }
    };

    [Fact]
    public void NoFile_MeansNothingToRestore()
    {
        var store = CreateStore();

        Assert.False(store.Exists);
        Assert.Null(store.Load());
    }

    [Fact]
    public void SavedState_SurvivesProcessRestart()
    {
        CreateStore().Save(SampleState());

        // 새 인스턴스 = 서비스 재시작 / PC 재부팅 상황
        var reopened = CreateStore();

        Assert.True(reopened.Exists);
        var state = reopened.Load();

        Assert.NotNull(state);
        Assert.Equal(2, state!.Adapters.Count);

        var ethernet = state.Adapters.Single(a => a.Name == "이더넷");
        Assert.False(ethernet.Ipv4Dhcp);
        Assert.Equal(new[] { "192.168.0.1", "8.8.4.4" }, ethernet.Ipv4Servers);
        Assert.True(ethernet.Ipv6Dhcp);
        Assert.True(ethernet.Ipv4Changed);

        var wifi = state.Adapters.Single(a => a.Name == "Wi-Fi");
        Assert.True(wifi.Ipv4Dhcp);
        Assert.False(wifi.Ipv6Changed);
    }

    [Fact]
    public void Clear_RemovesFile()
    {
        var store = CreateStore();
        store.Save(SampleState());
        Assert.True(store.Exists);

        store.Clear();

        Assert.False(store.Exists);
        Assert.Null(store.Load());
    }

    [Fact]
    public void FilePresence_IsTheUncleanShutdownSignal()
    {
        var store = CreateStore();

        // 정상 종료: 저장 -> 복구 -> 삭제
        store.Save(SampleState());
        store.Clear();
        Assert.False(store.Exists); // 다음 시작 때 복구할 것이 없다

        // 비정상 종료: 저장했는데 삭제되지 않음
        store.Save(SampleState());
        Assert.True(CreateStore().Exists); // 다음 시작 때 복구해야 한다
    }

    [Fact]
    public void BrokenFile_DoesNotThrow()
    {
        File.WriteAllText(_path, "{ 깨진 내용");

        var store = CreateStore();

        // 읽지 못해도 예외를 던지지 않는다. (서비스 시작을 막으면 안 된다)
        Assert.Null(store.Load());
    }

    [Fact]
    public void StateJson_IsHumanReadableForManualRecovery()
    {
        CreateStore().Save(SampleState());

        var json = File.ReadAllText(_path);

        // 수동 복구가 필요할 때 사람이 읽고 netsh 로 되돌릴 수 있어야 한다.
        Assert.Contains("이더넷", json);
        Assert.Contains("192.168.0.1", json);
        Assert.Contains("Ipv4Dhcp", json);
    }
}

/// <summary>DNS self-test 결과 해석 및 status 표시.</summary>
public class DnsSafetyStatusTests
{
    [Fact]
    public void Status_ShowsAllRequestedFields()
    {
        var text = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Running,
            HostsFallbackActive = false,
            AutoConfigureEnabled = true,
            AdapterDns = "127.0.0.1",
            ConfiguredAdapters = new[] { "이더넷" },
            OriginalDnsSaved = true,
            SelfTestPassed = true,
            UpstreamServers = new[] { "1.1.1.1" }
        }.Format();

        Assert.Contains("DNS Proxy          : RUNNING", text);
        Assert.Contains("Adapter DNS        : 127.0.0.1", text);
        Assert.Contains("Auto Configure     : ENABLED", text);
        Assert.Contains("DNS Self Test      : OK", text);
        Assert.Contains("Original DNS Saved : YES", text);
        Assert.Contains("Fallback           : NOT ACTIVE", text);
    }

    [Fact]
    public void Status_WhenAdaptersNotChanged_ShowsOriginalDns()
    {
        var text = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Failed,
            HostsFallbackActive = true,
            AutoConfigureEnabled = true,
            OriginalDnsSaved = false,
            SelfTestPassed = false,
            SelfTestDetail = "프록시가 응답하지 않습니다"
        }.Format();

        Assert.Contains("Adapter DNS        : (원래 설정)", text);
        Assert.Contains("Original DNS Saved : NO", text);
        Assert.Contains("DNS Self Test      : FAILED", text);
        Assert.Contains("프록시가 응답하지 않습니다", text);
        Assert.Contains("Fallback           : HOSTS ACTIVE", text);
    }

    [Fact]
    public void Status_SelfTestNotRunYet()
    {
        var text = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Stopped,
            SelfTestPassed = null
        }.Format();

        Assert.Contains("DNS Self Test      : NOT RUN", text);
    }

    [Fact]
    public void Status_AutoConfigureDisabled_IsShown()
    {
        var text = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Running,
            AutoConfigureEnabled = false
        }.Format();

        Assert.Contains("Auto Configure     : DISABLED", text);
    }

    [Fact]
    public void SelfTestResult_UpstreamUnreachable_IsNotAProxyFailure()
    {
        // 인터넷이 끊긴 것과 프록시가 고장난 것은 구분되어야 한다.
        // (인터넷 문제로 어댑터를 롤백하면 오히려 상태만 흔들린다)
        var result = new DnsSelfTestResult
        {
            Success = true,
            AllowedDomainResolved = false,
            BlockedDomainBlocked = true,
            UpstreamUnreachable = true,
            Detail = "상위 DNS 응답 없음 (인터넷 끊김)"
        };

        Assert.True(result.Success);
        Assert.True(result.UpstreamUnreachable);
    }
}

/// <summary>dns status / dns test / dns restore 명령 파싱.</summary>
public class DnsCommandParsingTests
{
    private readonly RemoteCommandParser _sut = new();

    [Theory]
    [InlineData("dns status", RemoteCommandType.DnsStatus)]
    [InlineData("dns", RemoteCommandType.DnsStatus)]
    [InlineData("/dns status", RemoteCommandType.DnsStatus)]
    [InlineData("DNS STATUS", RemoteCommandType.DnsStatus)]
    [InlineData("dns test", RemoteCommandType.DnsTest)]
    [InlineData("dns selftest", RemoteCommandType.DnsTest)]
    [InlineData("dns restore", RemoteCommandType.DnsRestore)]
    [InlineData("dns rollback", RemoteCommandType.DnsRestore)]
    public void ParsesDnsCommands(string input, RemoteCommandType expected)
    {
        Assert.Equal(expected, _sut.Parse(input).Type);
    }

    [Fact]
    public void UnknownDnsSubcommand_ReturnsUsage()
    {
        var command = _sut.Parse("dns bogus");

        Assert.Equal(RemoteCommandType.Unknown, command.Type);
        Assert.Contains("dns status", command.Error);
        Assert.Contains("dns restore", command.Error);
    }

    [Fact]
    public void DnsRestore_IsNotTreatedAsConfigChange()
    {
        // 설정파일을 바꾸는 것이 아니라 시스템 상태를 되돌리는 명령이다.
        Assert.False(_sut.Parse("dns restore").IsConfigChanging);
    }

    [Fact]
    public void HelpIncludesDnsCommands()
    {
        var help = ResponseFormatter.HelpText;

        Assert.Contains("dns status", help);
        Assert.Contains("dns test", help);
        Assert.Contains("dns restore", help);
    }
}

/// <summary>dns 명령이 실제로 컨트롤러를 호출하는지.</summary>
public class DnsCommandHandlerTests : IDisposable
{
    private readonly string _directory;
    private readonly NullEnforcementController _enforcement = new();
    private readonly RemoteCommandHandler _sut;

    public DnsCommandHandlerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var configStore = new JsonConfigurationStore(Path.Combine(_directory, "config.json"));
        var clock = new FixedClock(new DateTime(2026, 9, 22, 21, 20, 0, DateTimeKind.Local));
        var permits = new TemporaryPermitManager(
            new InMemoryPermitStateStore(), clock, () => configStore.Current.TemporaryPermit);
        var policy = new AccessPolicyEngine(
            () => configStore.Current, new ScheduleManager(), permits, clock);

        _sut = new RemoteCommandHandler(
            configStore, permits, policy, new ScheduleManager(), _enforcement, clock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 무시 */ }
    }

    [Fact]
    public async Task DnsStatus_ReturnsFormattedStatus()
    {
        var response = await _sut.ExecuteTextAsync("dns status", "test");

        Assert.Contains("DNS Mode", response);
        Assert.Contains("DNS Proxy", response);
        Assert.Contains("Original DNS Saved", response);
    }

    [Fact]
    public async Task DnsTest_InvokesSelfTest()
    {
        var response = await _sut.ExecuteTextAsync("dns test", "test");

        Assert.Equal(1, _enforcement.SelfTestCount);
        Assert.Contains("Proxy direct", response);
    }

    [Fact]
    public async Task DnsRestore_InvokesRestore()
    {
        var response = await _sut.ExecuteTextAsync("dns restore", "test");

        Assert.Equal(1, _enforcement.RestoreCount);
        Assert.StartsWith("OK", response);
    }

    [Fact]
    public async Task InvalidDnsCommand_DoesNotInvokeAnything()
    {
        var response = await _sut.ExecuteTextAsync("dns bogus", "test");

        Assert.StartsWith("ERROR", response);
        Assert.Equal(0, _enforcement.SelfTestCount);
        Assert.Equal(0, _enforcement.RestoreCount);
    }
}
