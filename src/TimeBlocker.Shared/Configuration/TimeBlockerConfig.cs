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
    public bool Enabled { get; set; } = true;

    /// <summary>차단할 도메인 목록. 코드에 하드코딩하지 않고 여기서 관리한다.</summary>
    public List<string> Domains { get; set; } = new();

    public bool UseDnsBlocking { get; set; } = true;

    public bool UseFirewallBlocking { get; set; }

    /// <summary>방화벽으로 막을 실행파일 이름(자동 탐색용).</summary>
    public List<string> ProcessNames { get; set; } = new();

    /// <summary>자동 탐색에 더해 수동으로 지정한 실행파일 전체 경로.</summary>
    public List<string> ExtraExecutablePaths { get; set; } = new();
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
}

/// <summary>TimeBlocker 전체 설정. timeblocker.config.json 으로 저장된다.</summary>
public sealed class TimeBlockerConfig
{
    public int ConfigVersion { get; set; } = 1;

    public WeeklySchedule Schedule { get; set; } = WeeklySchedule.CreateDefault();

    public DnsSettings Dns { get; set; } = new();

    public TargetSettings YouTube { get; set; } = new();

    public TargetSettings Roblox { get; set; } = new();

    public TemporaryPermitSettings TemporaryPermit { get; set; } = new();

    public TelegramSettings Telegram { get; set; } = new();

    public SecuritySettings Security { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    public EnforcementSettings Enforcement { get; set; } = new();

    public TargetSettings GetTarget(BlockTarget target) => target switch
    {
        BlockTarget.YouTube => YouTube,
        BlockTarget.Roblox => Roblox,
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
                Enabled = true,
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
                Enabled = true,
                UseDnsBlocking = true,
                UseFirewallBlocking = true,
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
            }
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
