using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Service.Blocking.BrowserPolicy;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// 브라우저 정책 차단(쇼츠 전용 경로 차단)의 동작 검증.
///
/// 실제 HKLM 을 건드리지 않도록 레지스트리는 가짜 구현으로 바꿔 끼운다.
/// 가짜는 실제 구현과 같은 규칙을 지킨다.
///   - 목록 키는 "1", "2" 같은 번호 값으로 이루어진다.
///   - 키가 없으면 ReadList 는 null 을 돌려준다.
///   - 빈 목록을 쓰면 키 자체가 사라진다.
/// </summary>
public class BrowserPolicyTests
{
    private const string ChromeKey = @"SOFTWARE\Policies\Google\Chrome";
    private static string ChromeUrls => $@"{ChromeKey}\{PolicyNames.UrlBlocklist}";

    // ================================================================= 적용

    [Fact]
    public void Apply_WritesOurPatterns()
    {
        var (manager, registry, _) = Create();

        var changed = manager.Apply(Config(), new[] { "youtube.com/shorts" });

        Assert.True(changed);
        Assert.Equal(new[] { "youtube.com/shorts" }, registry.ReadList(ChromeUrls));
    }

    [Fact]
    public void Apply_KeepsExistingUserEntries()
    {
        var (manager, registry, _) = Create();

        // 부모가 이미 손으로 넣어둔 항목이 있는 상황.
        registry.WriteList(ChromeUrls, new[] { "example.com/bad" });

        manager.Apply(Config(), new[] { "youtube.com/shorts" });

        Assert.Equal(
            new[] { "example.com/bad", "youtube.com/shorts" },
            registry.ReadList(ChromeUrls));
    }

    [Fact]
    public void Apply_Twice_DoesNotWriteAgain()
    {
        var (manager, registry, _) = Create();
        var config = Config();

        manager.Apply(config, new[] { "youtube.com/shorts" });
        var writesAfterFirst = registry.WriteCount;

        var changed = manager.Apply(config, new[] { "youtube.com/shorts" });

        Assert.False(changed);
        Assert.Equal(writesAfterFirst, registry.WriteCount);
    }

    [Fact]
    public void Apply_WritesGuardPolicies()
    {
        var (manager, registry, _) = Create();

        manager.Apply(Config(), new[] { "youtube.com/shorts" });

        Assert.Equal(
            PolicyValue.Dword(1),
            registry.ReadValue(ChromeKey, PolicyNames.IncognitoModeAvailability));
        Assert.Equal(
            PolicyValue.Dword(0),
            registry.ReadValue(ChromeKey, PolicyNames.BrowserGuestModeEnabled));
        Assert.Equal(
            PolicyValue.Text("off"),
            registry.ReadValue(ChromeKey, PolicyNames.DnsOverHttpsMode));
    }

    [Fact]
    public void Apply_GuardPoliciesStayWhenNotBlocked()
    {
        var (manager, registry, _) = Create();
        var config = Config();

        // 쇼츠가 허용된 상태(일시 허용 등)여도 우회 경로는 계속 막아둔다.
        // 차단 시간 직전에 시크릿 창을 미리 열어두는 우회를 막기 위한 것이다.
        manager.Apply(config, Array.Empty<string>());

        Assert.Null(registry.ReadList(ChromeUrls));
        Assert.Equal(
            PolicyValue.Dword(1),
            registry.ReadValue(ChromeKey, PolicyNames.IncognitoModeAvailability));
    }

    [Fact]
    public void Apply_RemovesOnlyOurEntries_WhenUnblocked()
    {
        var (manager, registry, _) = Create();
        var config = Config();

        registry.WriteList(ChromeUrls, new[] { "example.com/bad" });
        manager.Apply(config, new[] { "youtube.com/shorts" });

        // 허용으로 바뀌면 우리 항목만 빠지고 부모가 넣은 항목은 남아야 한다.
        manager.Apply(config, Array.Empty<string>());

        Assert.Equal(new[] { "example.com/bad" }, registry.ReadList(ChromeUrls));
    }

    [Fact]
    public void Apply_SkipsPatternWithWhitespace()
    {
        var (manager, registry, _) = Create();

        // 공백이 들어간 항목은 Chromium 이 통째로 무시하므로 아예 쓰지 않는다.
        manager.Apply(Config(), new[] { "youtube.com/shorts", "bad pattern/x" });

        Assert.Equal(new[] { "youtube.com/shorts" }, registry.ReadList(ChromeUrls));
    }

    [Fact]
    public void Apply_SkipsUnknownBrowser_AndDoesNotGuess()
    {
        var (manager, registry, store) = Create();

        var config = Config();
        config.BrowserPolicy.Browsers = new List<string> { "madeupbrowser" };

        var changed = manager.Apply(config, new[] { "youtube.com/shorts" });

        Assert.False(changed);
        Assert.Equal(0, registry.WriteCount);
        Assert.False(store.Exists);
    }

    [Fact]
    public void Apply_UsesRegistryKeyOverride()
    {
        var (manager, registry, _) = Create();

        var config = Config();
        config.BrowserPolicy.Browsers = new List<string> { "whale" };
        config.BrowserPolicy.RegistryKeyOverrides = new Dictionary<string, string>
        {
            ["whale"] = @"HKLM\SOFTWARE\Policies\Naver\Whale"
        };

        manager.Apply(config, new[] { "youtube.com/shorts" });

        // HKLM 접두사는 떼고 쓴다.
        Assert.Equal(
            new[] { "youtube.com/shorts" },
            registry.ReadList($@"SOFTWARE\Policies\Naver\Whale\{PolicyNames.UrlBlocklist}"));
    }

    [Fact]
    public void Apply_BlocksExtensionInstalls_WhenEnabled()
    {
        var (manager, registry, _) = Create();

        var config = Config();
        config.BrowserPolicy.BlockExtensionInstalls = true;
        config.BrowserPolicy.ExtensionAllowlist = new List<string> { "abcdefghijklmnop" };

        manager.Apply(config, new[] { "youtube.com/shorts" });

        Assert.Equal(
            new[] { "*" },
            registry.ReadList($@"{ChromeKey}\{PolicyNames.ExtensionInstallBlocklist}"));
        Assert.Equal(
            new[] { "abcdefghijklmnop" },
            registry.ReadList($@"{ChromeKey}\{PolicyNames.ExtensionInstallAllowlist}"));
    }

    [Fact]
    public void Apply_DoesNotTouchExtensionPolicies_WhenDisabled()
    {
        var (manager, registry, _) = Create();

        manager.Apply(Config(), new[] { "youtube.com/shorts" });

        Assert.Null(registry.ReadList($@"{ChromeKey}\{PolicyNames.ExtensionInstallBlocklist}"));
        Assert.Null(registry.ReadList($@"{ChromeKey}\{PolicyNames.ExtensionInstallAllowlist}"));
    }

    // ================================================================= 백업

    [Fact]
    public void Apply_SavesBackupBeforeWriting()
    {
        var (manager, _, store) = Create();

        Assert.False(store.Exists);

        manager.Apply(Config(), new[] { "youtube.com/shorts" });

        Assert.True(store.Exists);
        Assert.NotNull(store.Load()!.Find("chrome"));
    }

    [Fact]
    public void Apply_DoesNotOverwriteBackupWithOurOwnValues()
    {
        var (manager, _, store) = Create();
        var config = Config();

        manager.Apply(config, new[] { "youtube.com/shorts" });
        manager.Apply(config, new[] { "youtube.com/shorts" });

        // 두 번째 적용에서 다시 백업을 뜨면 우리가 쓴 값이 "원본"으로 기록되어
        // 영구히 되돌릴 수 없게 된다. 원본은 처음 그대로여야 한다.
        var backup = store.Load()!.Find("chrome")!;

        Assert.False(backup.FindList(PolicyNames.UrlBlocklist)!.Existed);
        Assert.False(backup.FindValue(PolicyNames.IncognitoModeAvailability)!.Existed);
    }

    [Fact]
    public void Apply_AddsBackupForBrowserAddedLater()
    {
        var (manager, _, store) = Create();
        var config = Config();

        manager.Apply(config, new[] { "youtube.com/shorts" });

        config.BrowserPolicy.Browsers = new List<string> { "chrome", "edge" };
        manager.Apply(config, new[] { "youtube.com/shorts" });

        var snapshot = store.Load()!;
        Assert.NotNull(snapshot.Find("chrome"));
        Assert.NotNull(snapshot.Find("edge"));
    }

    [Fact]
    public void Apply_DoesNotWrite_WhenBackupSaveFails()
    {
        var registry = new FakeRegistry();
        var store = new ThrowingStateStore();
        var manager = new BrowserPolicyManager(registry, store, NullLogger<BrowserPolicyManager>.Instance);

        var changed = manager.Apply(Config(), new[] { "youtube.com/shorts" });

        // 되돌릴 근거를 저장하지 못했으면 레지스트리를 건드려서는 안 된다.
        Assert.False(changed);
        Assert.Equal(0, registry.WriteCount);
        Assert.Null(registry.ReadList(ChromeUrls));
    }

    // ================================================================= 복구

    [Fact]
    public void Restore_PutsEverythingBack()
    {
        var (manager, registry, store) = Create();

        // 설치 전 상태: 부모가 넣어둔 차단 목록 + 시크릿 정책이 이미 있었다.
        registry.WriteList(ChromeUrls, new[] { "example.com/bad" });
        registry.WriteValue(ChromeKey, PolicyNames.IncognitoModeAvailability, PolicyValue.Dword(0));

        manager.Apply(Config(), new[] { "youtube.com/shorts" });
        manager.Restore();

        Assert.Equal(new[] { "example.com/bad" }, registry.ReadList(ChromeUrls));
        Assert.Equal(
            PolicyValue.Dword(0),
            registry.ReadValue(ChromeKey, PolicyNames.IncognitoModeAvailability));
        Assert.False(store.Exists);
    }

    [Fact]
    public void Restore_RemovesKeysThatDidNotExistBefore()
    {
        var (manager, registry, store) = Create();

        manager.Apply(Config(), new[] { "youtube.com/shorts" });
        manager.Restore();

        Assert.Null(registry.ReadList(ChromeUrls));
        Assert.Null(registry.ReadValue(ChromeKey, PolicyNames.IncognitoModeAvailability));
        Assert.Null(registry.ReadValue(ChromeKey, PolicyNames.DnsOverHttpsMode));
        Assert.False(store.Exists);
    }

    [Fact]
    public void Restore_WithoutBackup_DoesNothing()
    {
        var (manager, registry, _) = Create();

        registry.WriteList(ChromeUrls, new[] { "example.com/bad" });
        var writes = registry.WriteCount;

        var changed = manager.Restore();

        // 백업이 없으면 우리가 넣은 것이 없다는 뜻이다. 추측으로 지우면 남의 설정을 날린다.
        Assert.False(changed);
        Assert.Equal(writes, registry.WriteCount);
        Assert.Equal(new[] { "example.com/bad" }, registry.ReadList(ChromeUrls));
    }

    [Fact]
    public void Apply_WhenDisabled_RestoresPreviousState()
    {
        var (manager, registry, store) = Create();
        var config = Config();

        manager.Apply(config, new[] { "youtube.com/shorts" });

        // 기능을 끄면 다음 적용 주기에 스스로 원래대로 돌아가야 한다.
        config.BrowserPolicy.Enabled = false;
        manager.Apply(config, new[] { "youtube.com/shorts" });

        Assert.Null(registry.ReadList(ChromeUrls));
        Assert.False(store.Exists);
    }

    [Fact]
    public void Apply_AfterDisableAndReEnable_CapturesFreshOriginal()
    {
        var (manager, registry, store) = Create();
        var config = Config();

        manager.Apply(config, new[] { "youtube.com/shorts" });

        config.BrowserPolicy.Enabled = false;
        manager.Apply(config, Array.Empty<string>());

        config.BrowserPolicy.Enabled = true;
        manager.Apply(config, new[] { "youtube.com/shorts" });

        Assert.Equal(new[] { "youtube.com/shorts" }, registry.ReadList(ChromeUrls));

        // 다시 켠 뒤에도 "원래는 없었다" 가 유지되어야 완전한 복구가 가능하다.
        Assert.False(store.Load()!.Find("chrome")!.FindList(PolicyNames.UrlBlocklist)!.Existed);
    }

    // ================================================================= 경로 해석

    [Theory]
    [InlineData("chrome", @"SOFTWARE\Policies\Google\Chrome")]
    [InlineData("CHROME", @"SOFTWARE\Policies\Google\Chrome")]
    [InlineData("edge", @"SOFTWARE\Policies\Microsoft\Edge")]
    public void Resolve_KnownBrowsers(string id, string expected)
    {
        Assert.Equal(expected, BrowserProfiles.Resolve(id, null));
    }

    [Fact]
    public void Resolve_UnknownBrowser_ReturnsNull()
    {
        Assert.Null(BrowserProfiles.Resolve("madeupbrowser", null));
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Policies\X\Y")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\X\Y")]
    [InlineData(@"SOFTWARE\Policies\X\Y")]
    [InlineData(@"\SOFTWARE\Policies\X\Y\")]
    public void Resolve_StripsHklmPrefixAndSlashes(string input)
    {
        var overrides = new Dictionary<string, string> { ["x"] = input };

        Assert.Equal(@"SOFTWARE\Policies\X\Y", BrowserProfiles.Resolve("x", overrides));
    }

    [Fact]
    public void Resolve_OverrideWinsOverBuiltIn()
    {
        var overrides = new Dictionary<string, string> { ["chrome"] = @"SOFTWARE\Policies\Custom\Chrome" };

        Assert.Equal(@"SOFTWARE\Policies\Custom\Chrome", BrowserProfiles.Resolve("chrome", overrides));
    }

    [Fact]
    public void VerifiedBrowsers_AreTheDefault()
    {
        // 추정 경로를 기본값에 넣으면 "막은 줄 알았는데 안 막힌" 상태가 조용히 생긴다.
        var defaults = new BrowserPolicySettings().Browsers;

        Assert.Equal(BrowserProfiles.VerifiedBrowserIds.OrderBy(x => x), defaults.OrderBy(x => x));
        Assert.All(defaults, id => Assert.False(BrowserProfiles.IsUnverified(id)));
    }

    // ================================================================= 헬퍼

    private static (BrowserPolicyManager Manager, FakeRegistry Registry, InMemoryStateStore Store) Create()
    {
        var registry = new FakeRegistry();
        var store = new InMemoryStateStore();
        var manager = new BrowserPolicyManager(registry, store, NullLogger<BrowserPolicyManager>.Instance);
        return (manager, registry, store);
    }

    private static TimeBlockerConfig Config()
    {
        var config = TimeBlockerConfig.CreateDefault();
        config.Normalize();
        config.BrowserPolicy.Enabled = true;
        config.BrowserPolicy.Browsers = new List<string> { "chrome" };
        return config;
    }

    /// <summary>
    /// 메모리 레지스트리. 실제 RegistryPolicyEditor 와 같은 규칙으로 동작한다.
    /// 목록 키는 번호 값("1", "2"...)을 가진 보통 키로 표현된다.
    /// </summary>
    private sealed class FakeRegistry : IRegistryPolicyEditor
    {
        private readonly Dictionary<string, Dictionary<string, PolicyValue>> _keys =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>쓰기 호출 횟수. "상태가 같으면 쓰지 않는다" 를 검증하는 데 쓴다.</summary>
        public int WriteCount { get; private set; }

        public bool KeyExists(string subKey) => _keys.ContainsKey(subKey);

        public PolicyValue? ReadValue(string subKey, string name) =>
            _keys.TryGetValue(subKey, out var values) && values.TryGetValue(name, out var value) ? value : null;

        public void WriteValue(string subKey, string name, PolicyValue value)
        {
            WriteCount++;
            if (!_keys.TryGetValue(subKey, out var values))
            {
                values = new Dictionary<string, PolicyValue>(StringComparer.OrdinalIgnoreCase);
                _keys[subKey] = values;
            }
            values[name] = value;
        }

        public void DeleteValue(string subKey, string name)
        {
            if (!_keys.TryGetValue(subKey, out var values)) return;
            if (values.Remove(name)) WriteCount++;
        }

        public IReadOnlyList<string>? ReadList(string subKey)
        {
            if (!_keys.TryGetValue(subKey, out var values)) return null;

            return values
                .Where(kv => int.TryParse(kv.Key, out _))
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.StringValue))
                .OrderBy(kv => int.Parse(kv.Key))
                .Select(kv => kv.Value.StringValue!)
                .ToList();
        }

        public void WriteList(string subKey, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                DeleteKey(subKey);
                return;
            }

            WriteCount++;

            var entries = new Dictionary<string, PolicyValue>(StringComparer.OrdinalIgnoreCase);
            if (_keys.TryGetValue(subKey, out var existing))
            {
                // 번호가 아닌 값은 그대로 둔다. (실제 구현과 동일)
                foreach (var kv in existing.Where(kv => !int.TryParse(kv.Key, out _))) entries[kv.Key] = kv.Value;
            }

            for (var i = 0; i < values.Count; i++) entries[(i + 1).ToString()] = PolicyValue.Text(values[i]);

            _keys[subKey] = entries;
        }

        public void DeleteKey(string subKey)
        {
            var prefix = subKey + "\\";
            var doomed = _keys.Keys
                .Where(k => k.Equals(subKey, StringComparison.OrdinalIgnoreCase)
                            || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in doomed)
            {
                _keys.Remove(key);
                WriteCount++;
            }
        }
    }

    /// <summary>
    /// 메모리 백업 저장소. 실제 저장소처럼 JSON 왕복을 거친다.
    /// 객체 참조를 그대로 들고 있으면 "저장하지 않은 변경이 반영되는" 가짜 성공이 생긴다.
    /// </summary>
    private sealed class InMemoryStateStore : IBrowserPolicyStateStore
    {
        private string? _json;

        public bool Exists => _json is not null;

        public BrowserPolicySnapshot? Load() =>
            _json is null ? null : JsonUtil.Deserialize<BrowserPolicySnapshot>(_json);

        public void Save(BrowserPolicySnapshot snapshot) => _json = JsonUtil.Serialize(snapshot);

        public void Clear() => _json = null;
    }

    /// <summary>디스크 저장이 실패하는 상황(권한 부족 / 디스크 꽉 찬 경우)을 재현한다.</summary>
    private sealed class ThrowingStateStore : IBrowserPolicyStateStore
    {
        public bool Exists => false;

        public BrowserPolicySnapshot? Load() => null;

        public void Save(BrowserPolicySnapshot snapshot) => throw new IOException("디스크에 쓸 수 없습니다.");

        public void Clear() { }
    }
}
