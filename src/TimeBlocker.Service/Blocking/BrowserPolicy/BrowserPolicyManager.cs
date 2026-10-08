using Microsoft.Extensions.Logging;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Blocking.BrowserPolicy;

public interface IBrowserPolicyManager
{
    /// <summary>
    /// 정책을 지금 상태에 맞춰 적용한다.
    /// urlPatterns 가 비어 있으면 URL 차단 목록만 걷어내고, 우회 봉쇄 항목은 그대로 둔다.
    /// </summary>
    /// <returns>실제로 레지스트리를 건드렸으면 true.</returns>
    bool Apply(TimeBlockerConfig config, IReadOnlyCollection<string> urlPatterns);

    /// <summary>저장된 백업으로 모든 정책을 원래대로 되돌린다. (cleanup / 기능 끄기)</summary>
    bool Restore();

    /// <summary>우리 백업이 남아 있는지. (= 정책을 바꿔놓았을 수 있는 상태)</summary>
    bool HasSavedOriginal { get; }

    /// <summary>status 표시용 한 줄 요약.</summary>
    string Describe(TimeBlockerConfig config);
}

/// <summary>
/// Chromium 계열 브라우저의 HKLM 정책을 써서 URL 경로 단위 차단을 적용한다.
///
/// 왜 필요한가:
///   DNS 차단은 도메인 단위다. 쇼츠와 일반 영상은 같은 youtube.com / googlevideo.com 을
///   쓰므로 DNS 로는 구분할 수 없다. 경로(/shorts)를 보는 수단은 브라우저 정책뿐이다.
///
/// 한계 (README 14장에도 적어 둔다):
///   - 실제 페이지 로드만 막는다. 유튜브 안에서 JS 로 주소만 바꾸는 이동(SPA)은 걸리지 않는다.
///     그래서 쇼츠 피드가 쓰는 내부 API 경로도 함께 막는다.
///   - 홈 화면의 쇼츠 섹션은 그대로 보인다.
///   - 정책이 적용되지 않는 브라우저로 옮기면 효과가 없다.
///
/// 안전 원칙 (어댑터 DNS 와 동일):
///   - 레지스트리를 건드리기 전에 반드시 원본을 파일로 저장한다. 저장 실패 시 쓰지 않는다.
///   - 사용자가 원래 가지고 있던 목록 항목은 지우지 않는다. 우리 항목만 더하고 뺀다.
///   - 어떤 단계가 실패해도 예외를 밖으로 던지지 않는다. 다음 주기에 다시 시도한다.
/// </summary>
public sealed class BrowserPolicyManager : IBrowserPolicyManager
{
    /// <summary>확장 전체 차단에 쓰는 와일드카드. Chromium 정책이 정한 값이다.</summary>
    private const string AllExtensions = "*";

    private readonly IRegistryPolicyEditor _registry;
    private readonly IBrowserPolicyStateStore _store;
    private readonly ILogger<BrowserPolicyManager> _logger;

    private readonly object _lock = new();

    /// <summary>경로가 수상한 패턴을 한 번만 경고하기 위한 기록.</summary>
    private readonly HashSet<string> _warnedPatterns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 브라우저 경고를 한 번만 남기기 위한 기록.
    /// 적용은 10초마다 돌고 status 도 같은 경로를 지나므로, 그대로 두면 로그가 같은 줄로 가득 찬다.
    /// </summary>
    private readonly HashSet<string> _warnedBrowsers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>권한 부족 경고를 매 주기 찍지 않기 위한 기록.</summary>
    private DateTimeOffset _lastAccessWarningUtc = DateTimeOffset.MinValue;

    private static readonly TimeSpan AccessWarningInterval = TimeSpan.FromMinutes(30);

    public BrowserPolicyManager(
        IRegistryPolicyEditor registry,
        IBrowserPolicyStateStore store,
        ILogger<BrowserPolicyManager> logger)
    {
        _registry = registry;
        _store = store;
        _logger = logger;
    }

    public bool HasSavedOriginal => _store.Exists;

    public bool Apply(TimeBlockerConfig config, IReadOnlyCollection<string> urlPatterns)
    {
        lock (_lock)
        {
            // 기능이 꺼져 있으면 우리가 넣어둔 것을 전부 되돌린다.
            // (설정을 끈 뒤에도 정책이 남아 있으면 원인을 찾기 매우 어렵다)
            if (!config.BrowserPolicy.Enabled) return RestoreCore();

            var targets = ResolveTargets(config.BrowserPolicy);
            if (targets.Count == 0)
            {
                // 10초마다 같은 줄을 남기지 않는다. 지속 상태는 doctor 가 매번 보고한다.
                if (WarnBrowserOnce("(none)"))
                {
                    _logger.LogWarning(
                        "브라우저 정책이 켜져 있지만 적용할 브라우저가 없습니다. " +
                        "BrowserPolicy.Browsers 를 확인하세요. " +
                        "(알 수 없는 이름은 RegistryKeyOverrides 로 경로를 지정해야 합니다)");
                }
                return false;
            }

            var patterns = SanitizePatterns(urlPatterns);

            try
            {
                var snapshot = EnsureBackup(targets);
                var changed = false;

                foreach (var (browserId, registryKey) in targets)
                {
                    var backup = snapshot.Find(browserId);
                    if (backup is null) continue; // EnsureBackup 이 보장하지만 방어적으로 둔다.

                    changed |= ApplyToBrowser(config.BrowserPolicy, browserId, registryKey, backup, patterns);
                }

                return changed;
            }
            catch (UnauthorizedAccessException ex)
            {
                WarnNoAccess(ex);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "브라우저 정책 적용에 실패했습니다. 다음 주기에 다시 시도합니다.");
                return false;
            }
        }
    }

    public bool Restore()
    {
        lock (_lock) return RestoreCore();
    }

    // ---------------------------------------------------------------- 적용

    private bool ApplyToBrowser(
        BrowserPolicySettings settings,
        string browserId,
        string registryKey,
        BrowserPolicyBackup backup,
        IReadOnlyList<string> patterns)
    {
        var changed = false;

        // 1. URL 차단 목록. 사용자가 원래 가지고 있던 항목은 유지하고 우리 패턴만 더한다.
        var originalUrls = backup.FindList(PolicyNames.UrlBlocklist);
        changed |= WriteMergedList(
            registryKey, PolicyNames.UrlBlocklist, originalUrls, patterns, browserId);

        // 2. 우회 봉쇄 항목.
        //    차단 시간이 아니어도 계속 적용한다. 시크릿 모드를 차단 시간에만 막으면
        //    "차단 시간이 되기 전에 시크릿 창을 미리 열어두는" 우회가 가능하다.
        changed |= WriteScalar(
            registryKey, PolicyNames.IncognitoModeAvailability,
            settings.DisableIncognito ? PolicyValue.Dword(1) : null, backup, browserId);

        changed |= WriteScalar(
            registryKey, PolicyNames.BrowserGuestModeEnabled,
            settings.DisableGuestMode ? PolicyValue.Dword(0) : null, backup, browserId);

        changed |= WriteScalar(
            registryKey, PolicyNames.DnsOverHttpsMode,
            settings.DisableDnsOverHttps ? PolicyValue.Text("off") : null, backup, browserId);

        // 3. 확장 설치 차단. 켜져 있을 때만 건드린다.
        var extensionBlocks = settings.BlockExtensionInstalls
            ? new[] { AllExtensions }
            : Array.Empty<string>();

        changed |= WriteMergedList(
            registryKey, PolicyNames.ExtensionInstallBlocklist,
            backup.FindList(PolicyNames.ExtensionInstallBlocklist), extensionBlocks, browserId);

        var allowlist = settings.BlockExtensionInstalls
            ? settings.ExtensionAllowlist.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList()
            : new List<string>();

        changed |= WriteMergedList(
            registryKey, PolicyNames.ExtensionInstallAllowlist,
            backup.FindList(PolicyNames.ExtensionInstallAllowlist), allowlist, browserId);

        return changed;
    }

    /// <summary>
    /// 목록형 정책을 "원래 항목 + 우리 항목" 으로 맞춘다.
    ///
    /// ours 가 비어 있으면 원래 상태로 되돌린다. 원래 키가 없었다면 키를 지운다.
    /// 내용이 이미 같으면 아무것도 쓰지 않는다. (10초마다 레지스트리를 쓰면 안 된다)
    /// </summary>
    private bool WriteMergedList(
        string registryKey,
        string policyName,
        PolicyListBackup? original,
        IReadOnlyCollection<string> ours,
        string browserId)
    {
        var path = $@"{registryKey}\{policyName}";

        var desired = new List<string>(original?.Values ?? new List<string>());
        foreach (var item in ours)
        {
            if (!desired.Contains(item, StringComparer.Ordinal)) desired.Add(item);
        }

        var current = _registry.ReadList(path);

        // 원래 키가 없고 우리가 넣을 것도 없으면 키 자체가 없어야 한다.
        if (desired.Count == 0)
        {
            if (current is null) return false;

            _registry.DeleteKey(path);
            _logger.LogInformation("{Browser}: {Policy} 를 제거했습니다.", browserId, policyName);
            return true;
        }

        if (current is not null && current.SequenceEqual(desired, StringComparer.Ordinal)) return false;

        _registry.WriteList(path, desired);
        _logger.LogInformation(
            "{Browser}: {Policy} 를 {Count}개로 적용했습니다. ({Items})",
            browserId, policyName, desired.Count, string.Join(", ", desired));
        return true;
    }

    /// <summary>
    /// 단일 값 정책을 맞춘다. desired 가 null 이면 원래 값으로 되돌린다.
    /// 이미 같은 값이면 쓰지 않는다.
    /// </summary>
    private bool WriteScalar(
        string registryKey,
        string policyName,
        PolicyValue? desired,
        BrowserPolicyBackup backup,
        string browserId)
    {
        var effective = desired ?? backup.FindValue(policyName)?.ToValue();
        var current = _registry.ReadValue(registryKey, policyName);

        if (effective is null)
        {
            // 원래 없던 값이고 지금도 쓰지 않는다 -> 지워야 한다.
            if (current is null) return false;

            _registry.DeleteValue(registryKey, policyName);
            _logger.LogInformation("{Browser}: {Policy} 를 제거했습니다.", browserId, policyName);
            return true;
        }

        if (current == effective) return false;

        _registry.WriteValue(registryKey, policyName, effective);
        _logger.LogInformation("{Browser}: {Policy} = {Value}", browserId, policyName, effective);
        return true;
    }

    // ---------------------------------------------------------------- 백업

    /// <summary>
    /// 아직 백업하지 않은 브라우저의 원본 정책을 저장한다.
    ///
    /// 레지스트리를 쓰기 <b>전에</b> 반드시 호출해야 한다. 저장이 실패하면 예외가 올라가고
    /// 호출한 쪽이 쓰기를 포기하므로, "되돌릴 수 없는 변경" 이 남지 않는다.
    ///
    /// 이미 백업이 있는 브라우저는 다시 읽지 않는다. 다시 읽으면 우리가 써 넣은 값을
    /// "원본" 으로 덮어써서 복구가 불가능해진다.
    /// </summary>
    private BrowserPolicySnapshot EnsureBackup(IReadOnlyList<(string BrowserId, string RegistryKey)> targets)
    {
        var snapshot = _store.Load() ?? new BrowserPolicySnapshot();
        var added = new List<string>();

        foreach (var (browserId, registryKey) in targets)
        {
            if (snapshot.Find(browserId) is not null) continue;

            snapshot.Browsers.Add(CaptureBackup(browserId, registryKey));
            added.Add(browserId);
        }

        if (added.Count == 0) return snapshot;

        snapshot.SavedAtUtc = DateTimeOffset.UtcNow;
        _store.Save(snapshot);
        _logger.LogInformation("브라우저 정책 원본을 백업했습니다: {Browsers}", string.Join(", ", added));

        return snapshot;
    }

    private BrowserPolicyBackup CaptureBackup(string browserId, string registryKey)
    {
        var backup = new BrowserPolicyBackup { BrowserId = browserId, RegistryKey = registryKey };

        foreach (var policyName in PolicyNames.ScalarPolicies)
        {
            var value = _registry.ReadValue(registryKey, policyName);
            backup.Values.Add(value is null
                ? PolicyValueBackup.Absent(policyName)
                : PolicyValueBackup.From(policyName, value));
        }

        foreach (var policyName in PolicyNames.ListPolicies)
        {
            var values = _registry.ReadList($@"{registryKey}\{policyName}");
            backup.Lists.Add(values is null
                ? PolicyListBackup.Absent(policyName)
                : PolicyListBackup.From(policyName, values));
        }

        return backup;
    }

    // ---------------------------------------------------------------- 복구

    private bool RestoreCore()
    {
        var snapshot = _store.Load();
        if (snapshot is null)
        {
            // 백업이 없으면 우리가 건드린 것도 없다. 레지스트리를 추측으로 지우지 않는다.
            return false;
        }

        var restored = 0;
        var failed = new List<string>();

        foreach (var backup in snapshot.Browsers)
        {
            try
            {
                RestoreBrowser(backup);
                restored++;
            }
            catch (Exception ex)
            {
                failed.Add(backup.BrowserId);
                _logger.LogError(ex,
                    @"{Browser} 정책 복구에 실패했습니다. HKLM\{Key} 를 직접 확인해야 할 수 있습니다.",
                    backup.BrowserId, backup.RegistryKey);
            }
        }

        if (failed.Count > 0)
        {
            // 일부라도 실패하면 백업을 지우지 않는다. 다음 기회에 다시 복구해야 한다.
            _logger.LogWarning("브라우저 정책 복구가 일부 실패했습니다: {Failed}", string.Join(", ", failed));
            return restored > 0;
        }

        _store.Clear();

        if (restored > 0) _logger.LogInformation("브라우저 정책 {Count}개를 원래대로 되돌렸습니다.", restored);
        return restored > 0;
    }

    private void RestoreBrowser(BrowserPolicyBackup backup)
    {
        foreach (var value in backup.Values)
        {
            var original = value.ToValue();
            if (original is null) _registry.DeleteValue(backup.RegistryKey, value.Name);
            else _registry.WriteValue(backup.RegistryKey, value.Name, original);
        }

        foreach (var list in backup.Lists)
        {
            var path = $@"{backup.RegistryKey}\{list.Name}";

            // 원래 키가 없었으면 키째로 지운다. WriteList 도 빈 목록이면 키를 지우지만
            // 의도를 분명히 하기 위해 갈라 둔다.
            if (!list.Existed) _registry.DeleteKey(path);
            else _registry.WriteList(path, list.Values);
        }
    }

    // ---------------------------------------------------------------- 보조

    /// <summary>
    /// 설정된 브라우저 이름을 (식별자, HKLM 키 경로) 목록으로 바꾼다.
    /// 경로를 알 수 없는 이름은 건너뛰고 경고한다. 추측해서 엉뚱한 키에 쓰지 않는다.
    /// </summary>
    /// <param name="warn">
    /// 경고 로그를 남길지. 상태 조회(Describe)에서는 끈다. 조회 때문에 로그가 늘어나면 안 된다.
    /// </param>
    private List<(string BrowserId, string RegistryKey)> ResolveTargets(
        BrowserPolicySettings settings, bool warn = true)
    {
        var results = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in settings.Browsers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var browserId = raw.Trim();
            if (!seen.Add(browserId)) continue;

            var key = BrowserProfiles.Resolve(browserId, settings.RegistryKeyOverrides);
            if (key is null)
            {
                if (warn && WarnBrowserOnce(browserId))
                {
                    _logger.LogWarning(
                        "알 수 없는 브라우저입니다: {Browser}. 정책 키 경로를 모르므로 건너뜁니다. " +
                        "BrowserPolicy.RegistryKeyOverrides 에 이 이름의 정책 키 경로를 지정하세요.",
                        browserId);
                }
                continue;
            }

            // 경로를 직접 지정했다면 사용자가 확인한 것으로 본다. 그때는 경고하지 않는다.
            var hasOverride = settings.RegistryKeyOverrides.Keys
                .Any(k => string.Equals(k.Trim(), browserId, StringComparison.OrdinalIgnoreCase));

            if (warn && BrowserProfiles.IsUnverified(browserId) && !hasOverride && WarnBrowserOnce(browserId))
            {
                _logger.LogWarning(
                    @"{Browser} 의 정책 키 경로(HKLM\{Key})는 확인되지 않은 추정값입니다. " +
                    "해당 브라우저에서 정책 페이지를 열어 실제로 적용됐는지 확인하세요.",
                    browserId, key);
            }

            results.Add((browserId, key));
        }

        return results;
    }

    /// <summary>
    /// URL 패턴을 다듬는다. 공백/중복을 걷어내고, 사이트 전체를 막아버릴 패턴에는 경고를 남긴다.
    /// </summary>
    private List<string> SanitizePatterns(IReadOnlyCollection<string> patterns)
    {
        var results = new List<string>();

        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var pattern = raw.Trim();

            // 정책 값에 공백이 들어가면 Chromium 이 항목 전체를 무시한다.
            if (pattern.Any(char.IsWhiteSpace))
            {
                WarnPatternOnce(pattern, "공백이 들어 있어 무시했습니다");
                continue;
            }

            // 경로가 없으면 그 사이트 전체가 막힌다.
            // 쇼츠만 막으려던 설정에서 이런 패턴이 보이면 거의 확실히 실수다.
            if (!pattern.Contains('/'))
            {
                WarnPatternOnce(pattern,
                    "경로(/)가 없어 해당 사이트 전체가 막힙니다. 쇼츠만 막으려면 youtube.com/shorts 처럼 경로까지 적으세요");
            }

            if (!results.Contains(pattern, StringComparer.Ordinal)) results.Add(pattern);
        }

        return results;
    }

    /// <summary>이 브라우저에 대해 아직 경고하지 않았으면 true 를 돌려주고 기록한다.</summary>
    private bool WarnBrowserOnce(string browserId)
    {
        lock (_warnedBrowsers) return _warnedBrowsers.Add(browserId);
    }

    private void WarnPatternOnce(string pattern, string reason)
    {
        lock (_warnedPatterns)
        {
            if (!_warnedPatterns.Add(pattern)) return;
        }

        _logger.LogWarning("URL 차단 패턴 확인이 필요합니다: {Pattern} - {Reason}", pattern, reason);
    }

    private void WarnNoAccess(Exception ex)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc - _lastAccessWarningUtc < AccessWarningInterval) return;

        _lastAccessWarningUtc = nowUtc;
        _logger.LogError(ex,
            "브라우저 정책을 쓸 권한이 없습니다. HKLM 정책 키 변경에는 관리자 권한이 필요합니다. " +
            "서비스가 LocalSystem 으로 도는지 확인하세요. (콘솔 모드라면 관리자 권한으로 실행)");
    }

    public string Describe(TimeBlockerConfig config)
    {
        if (!config.BrowserPolicy.Enabled)
        {
            return _store.Exists
                ? "Disabled (복구 대기 중 - 다음 주기에 원래 정책으로 되돌립니다)"
                : "Disabled";
        }

        var targets = ResolveTargets(config.BrowserPolicy, warn: false);
        if (targets.Count == 0) return "Enabled (적용 가능한 브라우저 없음)";

        var guards = new List<string>();
        if (config.BrowserPolicy.DisableIncognito) guards.Add("시크릿 차단");
        if (config.BrowserPolicy.DisableGuestMode) guards.Add("게스트 차단");
        if (config.BrowserPolicy.DisableDnsOverHttps) guards.Add("DoH 끔");
        if (config.BrowserPolicy.BlockExtensionInstalls) guards.Add("확장 설치 차단");

        var browsers = string.Join(", ", targets.Select(t => t.BrowserId));
        var guardText = guards.Count > 0 ? string.Join(" / ", guards) : "우회 봉쇄 없음";

        return $"{browsers} ({guardText})";
    }
}
