namespace TimeBlocker.Service.Blocking.BrowserPolicy;

/// <summary>
/// 브라우저별 HKLM 정책 키 경로.
///
/// Chromium 계열은 정책 이름(URLBlocklist, IncognitoModeAvailability ...)이 같고
/// 키 경로만 제조사별로 다르다. 그래서 경로만 여기서 관리한다.
///
/// chrome / edge 는 각 제조사 공식 문서로 확인된 경로다.
/// brave / whale 은 Chromium 계열이라는 점에 근거한 추정이므로 기본 대상에서 빼 두었다.
/// 실제로 쓰려면 해당 브라우저에서 정책 페이지(brave://policy, whale://policy)를 열어
/// 적용 여부를 먼저 확인해야 한다. 경로가 다르면 설정파일의 RegistryKeyOverrides 로 바로잡을 수 있다.
/// </summary>
public static class BrowserProfiles
{
    /// <summary>확인된 경로만 기본값으로 쓴다.</summary>
    public static readonly string[] VerifiedBrowserIds = { "chrome", "edge" };

    private static readonly Dictionary<string, string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // 공식 문서로 확인됨
        ["chrome"] = @"SOFTWARE\Policies\Google\Chrome",
        ["edge"] = @"SOFTWARE\Policies\Microsoft\Edge",

        // Chromium 계열이라는 점에 근거한 추정. 반드시 <brand>://policy 로 확인할 것.
        ["brave"] = @"SOFTWARE\Policies\BraveSoftware\Brave-Browser",
        ["whale"] = @"SOFTWARE\Policies\Naver\Whale",
        ["opera"] = @"SOFTWARE\Policies\Opera Software\Opera"
    };

    /// <summary>경로가 추정인 브라우저. 로그에 분명히 남기기 위해 구분해 둔다.</summary>
    public static bool IsUnverified(string browserId) =>
        KnownKeys.ContainsKey(browserId) && !VerifiedBrowserIds.Contains(browserId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 브라우저 식별자를 HKLM 정책 키 경로로 바꾼다.
    /// overrides 에 적힌 값이 항상 우선한다. (내장 목록이 틀렸을 때 코드 수정 없이 고칠 수 있다)
    /// </summary>
    public static string? Resolve(string browserId, IReadOnlyDictionary<string, string>? overrides)
    {
        if (string.IsNullOrWhiteSpace(browserId)) return null;

        var id = browserId.Trim();

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (!string.Equals(key.Trim(), id, StringComparison.OrdinalIgnoreCase)) continue;
                return Normalize(value);
            }
        }

        return KnownKeys.TryGetValue(id, out var known) ? Normalize(known) : null;
    }

    /// <summary>
    /// 앞뒤 역슬래시를 떼고 HKLM 접두사를 제거한다.
    /// 설정파일에 "HKLM\SOFTWARE\..." 로 적어도 동작하게 한다.
    /// </summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var text = path.Trim().Trim('\\');

        foreach (var prefix in new[] { @"HKEY_LOCAL_MACHINE\", @"HKLM\" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim('\\');
                break;
            }
        }

        return text.Length == 0 ? null : text;
    }
}

/// <summary>Chromium 계열 공통 정책 이름. 오타를 막기 위해 한 곳에 모아 둔다.</summary>
public static class PolicyNames
{
    /// <summary>차단할 URL 목록. 번호 값으로 된 하위 키.</summary>
    public const string UrlBlocklist = "URLBlocklist";

    /// <summary>설치를 막을 확장 ID 목록. "*" 은 전체 차단.</summary>
    public const string ExtensionInstallBlocklist = "ExtensionInstallBlocklist";

    /// <summary>전체 차단 중에도 허용할 확장 ID 목록.</summary>
    public const string ExtensionInstallAllowlist = "ExtensionInstallAllowlist";

    /// <summary>0=기본, 1=시크릿 사용 불가, 2=시크릿 강제.</summary>
    public const string IncognitoModeAvailability = "IncognitoModeAvailability";

    /// <summary>게스트 모드 허용 여부.</summary>
    public const string BrowserGuestModeEnabled = "BrowserGuestModeEnabled";

    /// <summary>"off" 이면 DNS over HTTPS 를 쓰지 않는다.</summary>
    public const string DnsOverHttpsMode = "DnsOverHttpsMode";

    /// <summary>목록형 정책 이름. 백업/복구에서 하위 키로 다룬다.</summary>
    public static readonly string[] ListPolicies =
    {
        UrlBlocklist,
        ExtensionInstallBlocklist,
        ExtensionInstallAllowlist
    };

    /// <summary>단일 값 정책 이름.</summary>
    public static readonly string[] ScalarPolicies =
    {
        IncognitoModeAvailability,
        BrowserGuestModeEnabled,
        DnsOverHttpsMode
    };
}
