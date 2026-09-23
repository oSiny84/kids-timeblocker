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
}
