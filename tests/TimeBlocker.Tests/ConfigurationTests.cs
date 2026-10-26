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

        // 쓸 확장은 미리 채워져 있어야 한다. 기능만 켜면 바로 쓸 수 있어야 하기 때문이다.
        Assert.NotEmpty(config.Shorts.ForcedExtensions);
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
    public void Shorts_ForcedExtensionsAreWellFormed()
    {
        // 형식이 틀리면 Chromium 이 조용히 무시한다. "막았는데 쇼츠가 되네" 로 돌아온다.
        // "<a~p 소문자 32글자>" 또는 "<ID>;<업데이트URL>" 이어야 한다.
        var config = TimeBlockerConfig.CreateDefault();
        config.Normalize();

        Assert.All(config.Shorts.ForcedExtensions, entry =>
        {
            var id = entry.Split(';', 2)[0];
            Assert.Equal(32, id.Length);
            Assert.All(id, c => Assert.InRange(c, 'a', 'p'));
        });
    }

    [Fact]
    public void ExplicitNullSections_AreFilledByNormalize()
    {
        File.WriteAllText(ConfigPath, """{ "Shorts": null, "BrowserPolicy": null }""");

        var store = new JsonConfigurationStore(ConfigPath);

        Assert.NotNull(store.Current.Shorts);
        Assert.NotNull(store.Current.BrowserPolicy);
        Assert.NotEmpty(store.Current.Shorts.ForcedExtensions);
    }

    [Fact]
    public void SuddenAttack_IsAddedToExistingInstalls()
    {
        // 1.3 이하에서 올라온 설정에는 SuddenAttack 구간이 없다.
        // 올리기만 하면 Roblox 와 같은 방식으로 차단 시간에 막혀야 한다. (설정파일을 고치게 만들지 않는다)
        File.WriteAllText(ConfigPath, """
                                      {
                                        "YouTube": { "Mode": "Blocked", "Domains": ["youtube.com"] },
                                        "Roblox":  { "Mode": "Schedule", "Domains": ["roblox.com"] }
                                      }
                                      """);

        var config = new JsonConfigurationStore(ConfigPath).Current;

        Assert.Equal(BlockMode.Schedule, config.SuddenAttack.Mode);
        Assert.True(config.SuddenAttack.UseDnsBlocking);
        Assert.True(config.SuddenAttack.UseFirewallBlocking);
        Assert.True(config.SuddenAttack.TerminateProcesses);
        Assert.Contains("SuddenAttack.exe", config.SuddenAttack.ProcessNames);
        Assert.Contains("sa.nexon.com", config.SuddenAttack.Domains);

        // 기존 대상의 상태는 그대로다.
        Assert.Equal(BlockMode.Blocked, config.YouTube.Mode);
    }

    [Fact]
    public void SuddenAttack_DoesNotBlockAllOfNexon()
    {
        // nexon.com 을 막으면 다른 넥슨 게임과 로그인까지 막힌다. 서든어택 홈페이지만 막는다.
        var config = TimeBlockerConfig.CreateDefault();

        Assert.DoesNotContain("nexon.com", config.SuddenAttack.Domains);
        Assert.All(config.SuddenAttack.Domains, d => Assert.EndsWith(".nexon.com", d));
    }

    [Fact]
    public void ExplicitNullSuddenAttack_IsFilledByNormalize()
    {
        File.WriteAllText(ConfigPath, """{ "SuddenAttack": null }""");

        var store = new JsonConfigurationStore(ConfigPath);

        Assert.NotNull(store.Current.SuddenAttack);
        Assert.Contains("SuddenAttack.exe", store.Current.SuddenAttack.ProcessNames);
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
