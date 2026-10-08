using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;
using Xunit;

namespace TimeBlocker.Tests;

public class ConfigurationTests : IDisposable
{
    private readonly string _directory;

    public ConfigurationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 정리 실패는 무시 */ }
    }

    private string ConfigPath => Path.Combine(_directory, "timeblocker.config.json");

    [Fact]
    public void CreatesDefaultConfigWhenMissing()
    {
        var store = new JsonConfigurationStore(ConfigPath);

        Assert.True(File.Exists(ConfigPath));
        Assert.NotEmpty(store.Current.YouTube.Domains);
        Assert.Contains("youtube.com", store.Current.YouTube.Domains);
    }

    [Fact]
    public void SaveThenReload_KeepsValues()
    {
        var store = new JsonConfigurationStore(ConfigPath);
        var config = store.Current;
        config.Schedule.GetDay(DayOfWeek.Monday).Start = "20:15";
        config.TemporaryPermit.MaxMinutes = 45;
        store.Save(config);

        var reopened = new JsonConfigurationStore(ConfigPath);

        Assert.Equal("20:15", reopened.Current.Schedule.GetDay(DayOfWeek.Monday).Start);
        Assert.Equal(45, reopened.Current.TemporaryPermit.MaxMinutes);
    }

    [Fact]
    public void BrokenConfig_FallsBackToDefaultsAndBacksUp()
    {
        File.WriteAllText(ConfigPath, "{ broken");

        var store = new JsonConfigurationStore(ConfigPath);

        Assert.NotNull(store.Current);
        Assert.Contains("youtube.com", store.Current.YouTube.Domains);
        Assert.True(File.Exists(ConfigPath + ".broken"));
    }

    [Fact]
    public void Normalize_FillsMissingPieces()
    {
        var config = new TimeBlockerConfig
        {
            Schedule = new WeeklySchedule(),
            Dns = new DnsSettings { UpstreamServers = new List<string>(), ProxyPort = 0 },
            TemporaryPermit = new TemporaryPermitSettings { MaxMinutes = 0 }
        };

        config.Normalize();

        Assert.Equal(7, config.Schedule.Days.Count);
        Assert.NotEmpty(config.Dns.UpstreamServers);
        Assert.Equal(53, config.Dns.ProxyPort);
        Assert.Equal(120, config.TemporaryPermit.MaxMinutes);
    }

    [Fact]
    public void ConfigJson_RoundTripsEnumsAsStrings()
    {
        var config = TimeBlockerConfig.CreateDefault();
        config.Dns.Mode = DnsBlockingMode.ProxyWithHostsFallback;

        var json = JsonUtil.Serialize(config);
        Assert.Contains("ProxyWithHostsFallback", json);

        var restored = JsonUtil.Deserialize<TimeBlockerConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal(DnsBlockingMode.ProxyWithHostsFallback, restored!.Dns.Mode);
    }

    [Fact]
    public void PasswordHash_VerifiesCorrectPasswordOnly()
    {
        var hash = PasswordHash.Create("secret-1234");

        Assert.True(PasswordHash.Verify(hash, "secret-1234"));
        Assert.False(PasswordHash.Verify(hash, "wrong"));
        Assert.False(PasswordHash.Verify(null, "secret-1234"));
        Assert.False(PasswordHash.Verify("garbage", "secret-1234"));
    }

    [Fact]
    public void SecretMask_NeverRevealsWholeToken()
    {
        const string token = "123456789:AAEabcdefghijklmnopqrstuvwxyz";
        var masked = SecretProtector.Mask(token);

        Assert.DoesNotContain("AAEabcdefghij", masked);
        Assert.Contains("****", masked);
    }

    [Fact]
    public void Shorts_DefaultsAreSafeForExistingInstalls()
    {
        // 1.2 이하에서 올라온 설정에는 Shorts 구간이 없다.
        // 올라오자마자 동작이 바뀌면 안 되므로, 기본값은 아무것도 막지 않는 상태여야 한다.
        File.WriteAllText(ConfigPath, """{ "YouTube": { "Domains": ["youtube.com"] } }""");

        var store = new JsonConfigurationStore(ConfigPath);
        var config = store.Current;

        Assert.False(config.BrowserPolicy.Enabled);
        Assert.Equal(BlockMode.Schedule, config.Shorts.Mode);

        // 패턴 자체는 미리 채워져 있어야 한다. 기능만 켜면 바로 쓸 수 있어야 하기 때문이다.
        Assert.Contains("youtube.com/shorts", config.Shorts.BlockedUrlPatterns);
    }

    [Fact]
    public void Shorts_DoesNotUseDnsOrFirewall()
    {
        // 쇼츠를 DNS 로 막으면 youtube.com 전체가 막혀버린다. 그 경로를 애초에 끊어 둔다.
        var config = TimeBlockerConfig.CreateDefault();
        config.Normalize();

        Assert.False(config.Shorts.UseDnsBlocking);
        Assert.False(config.Shorts.UseFirewallBlocking);
        Assert.False(config.Shorts.TerminateProcesses);
        Assert.True(config.Shorts.UseBrowserPolicyBlocking);
        Assert.Empty(config.Shorts.Domains);
    }

    [Fact]
    public void Shorts_PatternsAlwaysIncludeAPath()
    {
        // 경로 없이 호스트만 적으면 유튜브 전체가 막힌다. 기본값에 그런 패턴이 있으면 안 된다.
        var config = TimeBlockerConfig.CreateDefault();
        config.Normalize();

        Assert.All(config.Shorts.BlockedUrlPatterns, pattern => Assert.Contains("/", pattern));
    }

    [Fact]
    public void ExplicitNullSections_AreFilledByNormalize()
    {
        File.WriteAllText(ConfigPath, """{ "Shorts": null, "BrowserPolicy": null }""");

        var store = new JsonConfigurationStore(ConfigPath);

        Assert.NotNull(store.Current.Shorts);
        Assert.NotNull(store.Current.BrowserPolicy);
        Assert.NotEmpty(store.Current.Shorts.BlockedUrlPatterns);
    }

    [Fact]
    public void GetTarget_ThrowsForAll()
    {
        var config = TimeBlockerConfig.CreateDefault();
        Assert.Throws<ArgumentOutOfRangeException>(() => config.GetTarget(BlockTarget.All));

        // 실제 대상은 모두 조회할 수 있어야 한다. 하나라도 빠지면 평가 중 예외가 난다.
        Assert.All(BlockTargets.Real, target => Assert.NotNull(config.GetTarget(target)));
    }

    // -------------------------------------------------- 1.1 이하 설정 호환

    [Theory]
    [InlineData(false, BlockMode.Open)]      // 차단 안 함 -> 항상 열림
    [InlineData(true, BlockMode.Schedule)]   // 차단 함     -> 스케줄대로
    public void LegacyEnabledField_IsMigratedToMode(bool legacyEnabled, BlockMode expected)
    {
        // 아들 PC 처럼 이미 1.1 로 돌던 설정이 그대로 읽혀야 한다.
        var json = $$"""
                     {
                       "YouTube": { "Enabled": {{(legacyEnabled ? "true" : "false")}}, "Domains": ["youtube.com"] },
                       "Roblox":  { "Enabled": {{(legacyEnabled ? "true" : "false")}}, "Domains": ["roblox.com"] }
                     }
                     """;
        File.WriteAllText(ConfigPath, json);

        var store = new JsonConfigurationStore(ConfigPath);

        Assert.Equal(expected, store.Current.YouTube.Mode);
        Assert.Equal(expected, store.Current.Roblox.Mode);

        // 도메인 같은 나머지 설정은 그대로 살아 있어야 한다.
        Assert.Contains("youtube.com", store.Current.YouTube.Domains);
    }

    [Fact]
    public void LegacyEnabledField_IsNotWrittenBackOnSave()
    {
        File.WriteAllText(ConfigPath, """{ "YouTube": { "Enabled": false } }""");

        var store = new JsonConfigurationStore(ConfigPath);
        store.Save(store.Current);

        // 한 번 변환한 뒤에는 옛 필드가 남아 혼란을 주면 안 된다.
        // (요일 설정에도 Enabled 가 있으므로 대상 설정만 다시 읽어 확인한다)
        var reopened = new JsonConfigurationStore(ConfigPath);
        Assert.Null(reopened.Current.YouTube.Enabled);
        Assert.Equal(BlockMode.Open, reopened.Current.YouTube.Mode);
        Assert.Contains("\"Mode\": \"Open\"", File.ReadAllText(ConfigPath));
    }
}
