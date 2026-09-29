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
    public void GetTarget_ThrowsForAll()
    {
        var config = TimeBlockerConfig.CreateDefault();
        Assert.Throws<ArgumentOutOfRangeException>(() => config.GetTarget(BlockTarget.All));
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
