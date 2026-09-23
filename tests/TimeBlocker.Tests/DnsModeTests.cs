using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// DNS 모드 기본값과 기존 설정 존중, status 표시 형식 검증.
/// </summary>
public class DnsModeTests : IDisposable
{
    private readonly string _directory;

    public DnsModeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 무시 */ }
    }

    private string ConfigPath => Path.Combine(_directory, "timeblocker.config.json");

    // ------------------------------------------------------------- 기본값

    [Fact]
    public void DefaultMode_IsProxyWithHostsFallback()
    {
        Assert.Equal(DnsBlockingMode.ProxyWithHostsFallback, new DnsSettings().Mode);
        Assert.Equal(DnsBlockingMode.ProxyWithHostsFallback, TimeBlockerConfig.CreateDefault().Dns.Mode);
    }

    [Fact]
    public void NewInstall_WritesProxyWithHostsFallback()
    {
        var store = new JsonConfigurationStore(ConfigPath);

        Assert.Equal(DnsBlockingMode.ProxyWithHostsFallback, store.Current.Dns.Mode);
        Assert.Contains("ProxyWithHostsFallback", File.ReadAllText(ConfigPath));
    }

    // ------------------------------------------------ 기존 사용자 설정 존중

    [Fact]
    public void ExistingHostsMode_IsNotOverwritten()
    {
        // 기존 사용자가 Hosts 로 명시해 둔 설정파일
        File.WriteAllText(ConfigPath, """
            {
              "ConfigVersion": 1,
              "Dns": { "Enabled": true, "Mode": "Hosts" }
            }
            """);

        var store = new JsonConfigurationStore(ConfigPath);

        Assert.Equal(DnsBlockingMode.Hosts, store.Current.Dns.Mode);

        // 저장해도 Hosts 가 유지되어야 한다.
        store.Save(store.Current);
        Assert.Equal(DnsBlockingMode.Hosts, new JsonConfigurationStore(ConfigPath).Current.Dns.Mode);
    }

    [Fact]
    public void ExistingProxyMode_IsNotOverwritten()
    {
        File.WriteAllText(ConfigPath, """
            { "Dns": { "Enabled": true, "Mode": "Proxy" } }
            """);

        Assert.Equal(DnsBlockingMode.Proxy, new JsonConfigurationStore(ConfigPath).Current.Dns.Mode);
    }

    [Fact]
    public void MissingModeProperty_FallsBackToNewDefault()
    {
        // Mode 를 적지 않은 오래된 설정파일 -> 새 기본값이 적용된다.
        File.WriteAllText(ConfigPath, """
            { "Dns": { "Enabled": true, "UpstreamServers": [ "9.9.9.9" ] } }
            """);

        var config = new JsonConfigurationStore(ConfigPath).Current;

        Assert.Equal(DnsBlockingMode.ProxyWithHostsFallback, config.Dns.Mode);
        Assert.Equal(new[] { "9.9.9.9" }, config.Dns.UpstreamServers);
    }

    [Fact]
    public void Normalize_DoesNotChangeExplicitMode()
    {
        var config = new TimeBlockerConfig { Dns = new DnsSettings { Mode = DnsBlockingMode.Hosts } };

        config.Normalize();

        Assert.Equal(DnsBlockingMode.Hosts, config.Dns.Mode);
    }

    // ------------------------------------------------------- status 표시

    [Fact]
    public void Status_ProxyRunning()
    {
        var status = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Running,
            HostsFallbackActive = false,
            UpstreamServers = new[] { "1.1.1.1" }
        };

        var text = status.Format();

        Assert.Contains("DNS Mode           : ProxyWithHostsFallback", text);
        Assert.Contains("DNS Proxy          : RUNNING", text);
        Assert.Contains("Fallback           : NOT ACTIVE", text);
        Assert.Contains("Upstream DNS       : 1.1.1.1", text);
    }

    [Fact]
    public void Status_ProxyFailed_ShowsHostsFallbackActive()
    {
        var status = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Failed,
            HostsFallbackActive = true,
            UpstreamServers = new[] { "1.1.1.1" }
        };

        var text = status.Format();

        Assert.Contains("DNS Mode           : ProxyWithHostsFallback", text);
        Assert.Contains("DNS Proxy          : FAILED", text);
        Assert.Contains("Fallback           : HOSTS ACTIVE", text);

        // 프록시가 죽었으면 상위 DNS 는 의미가 없으므로 표시하지 않는다.
        Assert.DoesNotContain("Upstream DNS", text);
    }

    [Fact]
    public void Status_HostsMode_HasNoFallbackConcept()
    {
        var text = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.Hosts,
            ProxyState = DnsProxyState.NotUsed
        }.Format();

        Assert.Contains("DNS Mode           : Hosts", text);
        Assert.Contains("DNS Proxy          : NOT USED", text);
        Assert.Contains("Fallback           : N/A", text);
    }

    [Fact]
    public void Status_ProxyOnlyMode_ShowsFallbackDisabled()
    {
        var text = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.Proxy,
            ProxyState = DnsProxyState.Failed,
            HostsFallbackActive = false
        }.Format();

        Assert.Contains("DNS Proxy          : FAILED", text);
        Assert.Contains("Fallback           : DISABLED (proxy only)", text);
    }

    [Fact]
    public void Status_DnsDisabled()
    {
        var text = DnsRuntimeStatus.Disabled(DnsBlockingMode.ProxyWithHostsFallback).Format();

        Assert.Contains("DNS Mode           : Disabled", text);
        Assert.Contains("DNS Proxy          : NOT USED", text);
    }

    [Fact]
    public void Status_ShowsLastErrorWhenFailed()
    {
        var text = new DnsRuntimeStatus
        {
            Mode = DnsBlockingMode.ProxyWithHostsFallback,
            ProxyState = DnsProxyState.Failed,
            HostsFallbackActive = true,
            LastError = "Cannot bind 127.0.0.1:53 (AddressAlreadyInUse)"
        }.Format();

        Assert.Contains("Last Error", text);
        Assert.Contains("AddressAlreadyInUse", text);

        // 오류 문자열에 민감정보가 섞이지 않는지 확인 (토큰 등)
        Assert.DoesNotContain("bot", text, StringComparison.OrdinalIgnoreCase);
    }
}
