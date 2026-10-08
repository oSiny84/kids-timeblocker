using TimeBlocker.Shared.Models;

namespace TimeBlocker.Shared.Configuration;

/// <summary>DNS 차단 방식.</summary>
public enum DnsBlockingMode
{
    /// <summary>
    /// hosts 파일에 TimeBlocker 관리 구간만 추가/삭제한다.
    /// 와일드카드를 지원하지 않으므로 등록한 도메인과 정확히 일치할 때만 차단된다.
    /// </summary>
    Hosts = 0,

    /// <summary>
    /// 127.0.0.1 로컬 DNS 프록시만 사용한다. 프록시가 뜨지 못하면 도메인 차단이 적용되지 않는다.
    /// 하위 도메인까지 막을 수 있다.
    /// </summary>
    Proxy = 1,

    /// <summary>
    /// 기본값. 프록시를 우선 사용하고, 시작하지 못하거나 치명적 오류가 나면 hosts 로 자동 폴백한다.
    /// </summary>
    ProxyWithHostsFallback = 2
}

public sealed class DnsSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 기본값은 ProxyWithHostsFallback 이다.
    /// YouTube 는 rr1---sn-xxxx.googlevideo.com 같은 동적 하위 도메인을 대량으로 쓰는데
    /// hosts 파일은 와일드카드를 지원하지 않아 이런 주소를 막지 못한다.
    /// 프록시가 뜨지 못하는 환경에서만 hosts 로 자동 폴백한다.
    ///
    /// 이미 설정파일에 Mode 가 적혀 있으면 그 값이 그대로 쓰인다. (기존 설정을 덮어쓰지 않는다)
    /// </summary>
    public DnsBlockingMode Mode { get; set; } = DnsBlockingMode.ProxyWithHostsFallback;

    /// <summary>프록시 모드에서 사용할 상위 DNS 서버.</summary>
    public List<string> UpstreamServers { get; set; } = new() { "1.1.1.1", "8.8.8.8" };

    /// <summary>프록시 리스닝 포트. 기본 53.</summary>
    public int ProxyPort { get; set; } = 53;

    /// <summary>프록시 모드일 때 네트워크 어댑터 DNS 를 127.0.0.1 로 자동 변경할지.</summary>
    public bool AutoConfigureAdapters { get; set; } = true;

    /// <summary>상위 DNS 질의 타임아웃(ms).</summary>
    public int UpstreamTimeoutMs { get; set; } = 3000;

    /// <summary>차단/허용이 바뀔 때 Windows DNS 캐시를 비울지.</summary>
    public bool FlushCacheOnChange { get; set; } = true;

    /// <summary>
    /// IPv6 DNS(::1)도 함께 프록시로 돌릴지.
    /// 끄면 Windows 가 IPv6 DNS 서버로 질의해 프록시를 우회할 수 있다.
    /// </summary>
    public bool ConfigureIpv6 { get; set; } = true;

    /// <summary>
    /// self-test 에 사용할 "정상적으로 해석되어야 하는" 도메인.
    /// 어댑터 DNS 를 바꾸기 전후로 이 도메인이 해석되는지 확인한다.
    /// </summary>
    public string SelfTestDomain { get; set; } = "example.com";
}

/// <summary>차단 대상 1개의 설정.</summary>
public sealed class TargetSettings
{
    /// <summary>이 대상의 상태. 기본은 스케줄대로.</summary>
    public BlockMode Mode { get; set; } = BlockMode.Schedule;

    /// <summary>
    /// 1.1 이하 설정파일 호환용. 새로 저장할 때는 기록하지 않는다.
    /// 값이 있으면 설정을 읽은 직후 Mode 로 옮기고 지운다. (MigrateLegacyTargetModes)
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>Mode 가 없는 구버전 설정을 읽었을 때 한 번만 변환한다.</summary>
    internal bool MigrateLegacyMode()
    {
        if (Enabled is null) return false;

        // Enabled=false 는 "차단하지 않음" 이었으므로 항상 열림에 해당한다.
        // Enabled=true 는 스케줄대로였다. Mode 가 이미 명시돼 있으면 그쪽을 존중한다.
        if (Mode == BlockMode.Schedule && Enabled == false) Mode = BlockMode.Open;

        Enabled = null;
        return true;
    }

    /// <summary>차단할 도메인 목록. 코드에 하드코딩하지 않고 여기서 관리한다.</summary>
    public List<string> Domains { get; set; } = new();

    public bool UseDnsBlocking { get; set; } = true;

    public bool UseFirewallBlocking { get; set; }

    /// <summary>
    /// 차단 시간에 이 프로세스가 돌고 있으면 유예 시간을 주고 종료할지.
    /// DNS/방화벽은 이미 실행 중인 게임의 접속을 끊지 못하는 경우가 있어 필요하다.
    /// </summary>
    public bool TerminateProcesses { get; set; }

    /// <summary>방화벽 차단 / 프로세스 종료 대상 실행파일 이름.</summary>
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>자동 탐색에 더해 수동으로 지정한 실행파일 전체 경로.</summary>
    public List<string> ExtraExecutablePaths { get; set; } = new();

    /// <summary>
    /// 브라우저 정책(URLBlocklist)으로 막을지.
    ///
    /// DNS 차단은 도메인 단위라 경로(/shorts)를 구분할 수 없다.
    /// "유튜브는 되고 쇼츠만 막기" 처럼 경로 단위 차단이 필요한 대상에 쓴다.
    /// </summary>
    public bool UseBrowserPolicyBlocking { get; set; }

    /// <summary>
    /// 차단할 URL 패턴. Chromium URLBlocklist 필터 형식을 그대로 쓴다.
    ///
    /// 호스트만 적으면 하위 도메인까지 포함되고, 경로는 접두사로 비교된다.
    /// 즉 youtube.com/shorts 하나로 www / m 하위 도메인의 /shorts/&lt;id&gt; 까지 걸린다.
    /// 끝에 와일드카드(*)는 쓸 수 없고 경로는 대소문자를 구분한다.
    ///
    /// 주의: 경로 없이 호스트만 적으면 그 사이트 전체가 막힌다.
    /// </summary>
    public List<string> BlockedUrlPatterns { get; set; } = new();

    /// <summary>
    /// Shorts 대상의 기본값.
    ///
    /// DNS / 방화벽 / 프로세스 종료는 모두 쓰지 않는다. 경로 단위 차단이 필요하므로
    /// 브라우저 정책만 사용한다.
    ///
    /// 기본 상태는 다른 대상과 같은 Schedule 이다.
    /// 쇼츠를 상시 차단하려면 block shorts, 시간대만 막으려면 그대로 두면 된다.
    /// 어느 쪽이든 BrowserPolicy.Enabled 가 켜져 있어야 실제로 적용된다.
    /// 그 기능은 설치 중 질문이나 텔레그램 'policy on' / 'block shorts' 로 켜진다.
    /// 설정파일을 직접 고치게 만들지 않는다. (이 프로그램은 대상 PC 에 설정 화면을 두지 않는다)
    /// </summary>
    public static TargetSettings CreateShortsDefault() => new()
    {
        Mode = BlockMode.Schedule,
        UseDnsBlocking = false,
        UseFirewallBlocking = false,
        TerminateProcesses = false,
        UseBrowserPolicyBlocking = true,
        BlockedUrlPatterns = new List<string>
        {
            // 쇼츠 시청 페이지. 호스트만 적었으므로 www / m 하위 도메인도 함께 걸린다.
            "youtube.com/shorts",

            // 쇼츠 피드가 다음 영상을 받아오는 내부 API.
            // 이걸 막으면 페이지 이동 없이 넘기는 경로(SPA)도 영상을 받지 못한다.
            "youtube.com/youtubei/v1/reel/"
        }
    };
}

/// <summary>
/// 브라우저 정책(HKLM 레지스트리)으로 하는 차단과 우회 봉쇄 설정.
///
/// URLBlocklist 는 경로 단위 차단을 할 수 있는 유일한 수단이지만,
/// 그것만 켜면 시크릿 모드 / 게스트 모드 / 다른 브라우저로 쉽게 빠져나갈 수 있다.
/// 그래서 우회 경로를 함께 막는 항목을 같은 곳에 둔다.
///
/// 레지스트리를 건드리므로 기본값은 꺼짐이다. 켜기 전에 README 를 읽어야 한다.
/// </summary>
public sealed class BrowserPolicySettings
{
    /// <summary>기본 꺼짐. 켜면 아래 항목이 HKLM 정책으로 적용된다.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 정책을 적용할 브라우저 식별자.
    /// chrome / edge 는 공식 문서로 확인된 경로를 쓴다.
    /// brave / whale / opera 는 추정 경로이므로 기본값에 넣지 않았다.
    /// </summary>
    public List<string> Browsers { get; set; } = new() { "chrome", "edge" };

    /// <summary>
    /// 내장 경로가 틀렸거나 목록에 없는 브라우저를 쓸 때 지정한다.
    /// 예: { "whale": "SOFTWARE\Policies\Naver\Whale" }
    /// </summary>
    public Dictionary<string, string> RegistryKeyOverrides { get; set; } = new();

    /// <summary>
    /// 시크릿 모드를 막을지.
    ///
    /// 끄면 안 된다에 가깝다. 강제 설치한 확장은 시크릿 창에서 기본적으로 동작하지 않고,
    /// URLBlocklist 는 시크릿에서도 적용되지만 정책 우회 수단을 하나 열어두는 셈이 된다.
    /// </summary>
    public bool DisableIncognito { get; set; } = true;

    /// <summary>게스트 모드를 막을지. 게스트 창은 별도 프로필이라 확장이 없다.</summary>
    public bool DisableGuestMode { get; set; } = true;

    /// <summary>
    /// 브라우저의 DNS over HTTPS 를 끌지.
    /// 켜져 있으면 DNS 차단 자체가 우회된다. (YouTube / Roblox 차단에도 영향)
    /// </summary>
    public bool DisableDnsOverHttps { get; set; } = true;

    /// <summary>
    /// 확장 설치를 전부 막을지. (VPN / 프록시 확장으로 우회하는 것을 막는다)
    /// 이미 쓰고 있는 확장까지 멈추게 할 수 있어 기본은 꺼짐이다.
    /// </summary>
    public bool BlockExtensionInstalls { get; set; }

    /// <summary>BlockExtensionInstalls 가 켜졌을 때도 허용할 확장 ID.</summary>
    public List<string> ExtensionAllowlist { get; set; } = new();
}

public sealed class TemporaryPermitSettings
{
    /// <summary>1회 요청으로 허용 가능한 최대 분. 이보다 큰 요청은 거부한다.</summary>
    public int MaxMinutes { get; set; } = 120;

    /// <summary>최소 분.</summary>
    public int MinMinutes { get; set; } = 1;
}

public sealed class TelegramSettings
{
    public bool Enabled { get; set; }

    /// <summary>DPAPI 로 암호화되어 저장된 봇 토큰(Base64). 평문으로 저장하지 않는다.</summary>
    public string? ProtectedBotToken { get; set; }

    /// <summary>명령을 허용할 Telegram 사용자 ID 목록. 비어 있으면 아무도 명령할 수 없다.</summary>
    public List<long> AllowedUserIds { get; set; } = new();

    /// <summary>long polling 대기 시간(초).</summary>
    public int PollTimeoutSeconds { get; set; } = 30;

    /// <summary>API 실패 시 재시도 간격(초).</summary>
    public int RetryDelaySeconds { get; set; } = 15;
}

public sealed class SecuritySettings
{
    /// <summary>관리자 비밀번호 PBKDF2 해시. 형식: PBKDF2$iterations$saltBase64$hashBase64</summary>
    public string? AdminPasswordHash { get; set; }

    /// <summary>true 이면 설정 변경/일시 허용 시 GUI 에서 비밀번호를 요구한다.</summary>
    public bool RequirePasswordForChanges { get; set; } = true;
}

public sealed class LoggingSettings
{
    /// <summary>TRACE / DEBUG / INFO / WARN / ERROR</summary>
    public string MinimumLevel { get; set; } = "INFO";

    /// <summary>파일 1개 최대 크기(MB).</summary>
    public int MaxFileSizeMb { get; set; } = 8;

    /// <summary>보관할 일수.</summary>
    public int RetainDays { get; set; } = 30;
}

public sealed class EnforcementSettings
{
    /// <summary>정책 재평가 주기(초).</summary>
    public int EvaluationIntervalSeconds { get; set; } = 10;

    /// <summary>시스템 시간이 이 분 이상 갑자기 변하면 이상 변경으로 기록한다.</summary>
    public int TimeJumpThresholdMinutes { get; set; } = 5;

    /// <summary>
    /// 차단 시간에 게임이 돌고 있을 때, 경고를 띄우고 종료하기까지 기다리는 시간(분).
    /// 0 이면 경고 없이 즉시 종료한다.
    /// </summary>
    public int TerminationGraceMinutes { get; set; } = 5;
}

/// <summary>TimeBlocker 전체 설정. timeblocker.config.json 으로 저장된다.</summary>
public sealed class TimeBlockerConfig
{
    public int ConfigVersion { get; set; } = 1;

    public WeeklySchedule Schedule { get; set; } = WeeklySchedule.CreateDefault();

    public DnsSettings Dns { get; set; } = new();

    public TargetSettings YouTube { get; set; } = new();

    public TargetSettings Roblox { get; set; } = new();

    /// <summary>
    /// YouTube Shorts 전용 대상. 브라우저 정책(URLBlocklist)으로만 막는다.
    /// 1.2 이하 설정파일에는 이 구간이 없으므로 Normalize 에서 기본값을 채운다.
    /// </summary>
    public TargetSettings Shorts { get; set; } = TargetSettings.CreateShortsDefault();

    public BrowserPolicySettings BrowserPolicy { get; set; } = new();

    public TemporaryPermitSettings TemporaryPermit { get; set; } = new();

    public TelegramSettings Telegram { get; set; } = new();

    public SecuritySettings Security { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    public EnforcementSettings Enforcement { get; set; } = new();

    public TargetSettings GetTarget(BlockTarget target) => target switch
    {
        BlockTarget.YouTube => YouTube,
        BlockTarget.Roblox => Roblox,
        BlockTarget.Shorts => Shorts,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "실제 대상만 조회할 수 있습니다.")
    };

    /// <summary>처음 설치 시 사용할 기본 설정.</summary>
    public static TimeBlockerConfig CreateDefault()
    {
        return new TimeBlockerConfig
        {
            Schedule = WeeklySchedule.CreateDefault(),
            YouTube = new TargetSettings
            {
                Mode = BlockMode.Schedule,
                UseDnsBlocking = true,
                UseFirewallBlocking = false,
                Domains = new List<string>
                {
                    "youtube.com",
                    "www.youtube.com",
                    "m.youtube.com",
                    "youtu.be",
                    "googlevideo.com",
                    "ytimg.com",
                    "youtubei.googleapis.com",
                    "yt3.ggpht.com"
                }
            },
            Roblox = new TargetSettings
            {
                Mode = BlockMode.Schedule,
                UseDnsBlocking = true,
                UseFirewallBlocking = true,
                TerminateProcesses = true,
                Domains = new List<string>
                {
                    "roblox.com",
                    "www.roblox.com",
                    "web.roblox.com",
                    "api.roblox.com",
                    "rbxcdn.com",
                    "setup.rbxcdn.com",
                    "assetgame.roblox.com"
                },
                ProcessNames = new List<string>
                {
                    "RobloxPlayerBeta.exe",
                    "RobloxStudioBeta.exe",
                    "RobloxPlayerLauncher.exe"
                }
            },
            Shorts = TargetSettings.CreateShortsDefault()
        };
    }

    /// <summary>설정파일이 일부 비어 있어도 서비스가 죽지 않도록 빈 곳을 기본값으로 채운다.</summary>
    public void Normalize()
    {
        Schedule ??= WeeklySchedule.CreateDefault();
        Schedule.Days ??= new List<DaySchedule>();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            _ = Schedule.GetDay(day);
        }

        Dns ??= new DnsSettings();
        Dns.UpstreamServers ??= new List<string>();
        if (Dns.UpstreamServers.Count == 0)
        {
            Dns.UpstreamServers.Add("1.1.1.1");
            Dns.UpstreamServers.Add("8.8.8.8");
        }
        if (Dns.ProxyPort is <= 0 or > 65535) Dns.ProxyPort = 53;
        if (Dns.UpstreamTimeoutMs < 200) Dns.UpstreamTimeoutMs = 3000;
        if (string.IsNullOrWhiteSpace(Dns.SelfTestDomain)) Dns.SelfTestDomain = "example.com";

        YouTube ??= new TargetSettings();
        YouTube.Domains ??= new List<string>();
        YouTube.ProcessNames ??= new List<string>();
        YouTube.ExtraExecutablePaths ??= new List<string>();

        Roblox ??= new TargetSettings();
        Roblox.Domains ??= new List<string>();
        Roblox.ProcessNames ??= new List<string>();
        Roblox.ExtraExecutablePaths ??= new List<string>();

        // Shorts 구간이 아예 없는 설정파일(1.2 이하)은 기본값을 채운다.
        // 구간이 있으면 비어 있어도 그대로 존중한다. (사용자가 비워둔 것일 수 있다)
        Shorts ??= TargetSettings.CreateShortsDefault();
        Shorts.Domains ??= new List<string>();
        Shorts.ProcessNames ??= new List<string>();
        Shorts.ExtraExecutablePaths ??= new List<string>();
        Shorts.BlockedUrlPatterns ??= new List<string>();

        YouTube.BlockedUrlPatterns ??= new List<string>();
        Roblox.BlockedUrlPatterns ??= new List<string>();

        // 1.1 이하에서 올라온 설정파일의 Enabled 를 Mode 로 옮긴다.
        YouTube.MigrateLegacyMode();
        Roblox.MigrateLegacyMode();
        Shorts.MigrateLegacyMode();

        BrowserPolicy ??= new BrowserPolicySettings();
        BrowserPolicy.Browsers ??= new List<string>();
        BrowserPolicy.RegistryKeyOverrides ??= new Dictionary<string, string>();
        BrowserPolicy.ExtensionAllowlist ??= new List<string>();

        TemporaryPermit ??= new TemporaryPermitSettings();
        if (TemporaryPermit.MaxMinutes <= 0) TemporaryPermit.MaxMinutes = 120;
        if (TemporaryPermit.MinMinutes <= 0) TemporaryPermit.MinMinutes = 1;

        Telegram ??= new TelegramSettings();
        Telegram.AllowedUserIds ??= new List<long>();
        if (Telegram.PollTimeoutSeconds is < 1 or > 60) Telegram.PollTimeoutSeconds = 30;
        if (Telegram.RetryDelaySeconds < 1) Telegram.RetryDelaySeconds = 15;

        Security ??= new SecuritySettings();

        Logging ??= new LoggingSettings();
        if (Logging.MaxFileSizeMb <= 0) Logging.MaxFileSizeMb = 8;
        if (Logging.RetainDays <= 0) Logging.RetainDays = 30;

        Enforcement ??= new EnforcementSettings();
        if (Enforcement.EvaluationIntervalSeconds < 1) Enforcement.EvaluationIntervalSeconds = 10;
        if (Enforcement.TimeJumpThresholdMinutes < 1) Enforcement.TimeJumpThresholdMinutes = 5;
    }
}
