using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Service.Blocking.BrowserPolicy;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;

namespace TimeBlocker.Service.Setup;

/// <summary>
/// 설치 시 로컬에서만 수행하는 설정 명령.
///
/// Telegram Bot Token 과 관리자 User ID 는 원격으로 바꿀 수 없고
/// 반드시 PC 앞에서 관리자 권한으로 설정해야 한다. (보안상 의도된 제약)
/// </summary>
public static class LocalSetupCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].TrimStart('-', '/').ToLowerInvariant();

        return command switch
        {
            "set-token" => SetToken(args),
            "set-admin" => SetAdmin(args),
            "enable-telegram" => SetTelegramEnabled(true),
            "disable-telegram" => SetTelegramEnabled(false),
            "show-config" => ShowConfig(),
            "cleanup" => await CleanupAsync(removeStateFiles: args.Length > 1 && args[1].Contains("state", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false),
            "dns-restore" => await RestoreDnsAsync().ConfigureAwait(false),
            "doctor" or "diag" => await DoctorAsync().ConfigureAwait(false),
            "help" or "h" or "?" => PrintUsage(0),
            _ => PrintUsage(1)
        };
    }

    private static int PrintUsage(int exitCode)
    {
        Console.WriteLine(
            """
            TimeBlocker.Service - 로컬 설정 명령

            TimeBlocker.Service.exe set-token <BotToken>
                Telegram Bot Token 을 DPAPI 로 암호화해서 저장합니다.

            TimeBlocker.Service.exe set-admin <TelegramUserId>
                명령을 허용할 Telegram 숫자 User ID 를 설정합니다. (기존 목록을 대체)

            TimeBlocker.Service.exe enable-telegram
            TimeBlocker.Service.exe disable-telegram
                Telegram 원격 제어를 켜고 끕니다.

            TimeBlocker.Service.exe show-config
                현재 설정을 보여줍니다. (Bot Token 은 마스킹됩니다)

            TimeBlocker.Service.exe cleanup
                hosts 차단 구간과 방화벽 규칙, 브라우저 정책을 제거하고
                어댑터 DNS 를 원래 설정으로 되돌립니다.
                (프로그램 제거 전에 실행)

            TimeBlocker.Service.exe dns-restore
                어댑터 DNS 만 즉시 원래 설정으로 되돌립니다.
                서비스가 멈춘 상태에서 인터넷이 되지 않을 때 사용하세요.

            TimeBlocker.Service.exe doctor
                설치/네트워크/차단/Telegram 상태를 한 번에 점검합니다.
                서비스가 멈춰 있어도 동작합니다.

            인자 없이 실행하면 서비스(또는 콘솔 모드)로 동작합니다.
            """);
        return exitCode;
    }

    private static bool RequireAdmin()
    {
        if (IsAdministrator()) return true;

        Console.Error.WriteLine("이 명령은 관리자 권한이 필요합니다. 관리자 권한 터미널에서 다시 실행하세요.");
        return false;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static int SetToken(string[] args)
    {
        if (!RequireAdmin()) return 1;

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("사용법: TimeBlocker.Service.exe set-token <BotToken>");
            return 1;
        }

        var token = args[1].Trim();
        var store = new JsonConfigurationStore(AppPaths.ConfigFile);
        var config = store.Current;

        config.Telegram.ProtectedBotToken = SecretProtector.Protect(token);
        store.Save(config);

        // 토큰 자체는 절대 출력하지 않는다.
        Console.WriteLine($"Bot Token 을 저장했습니다. ({SecretProtector.Mask(token)})");
        return 0;
    }

    private static int SetAdmin(string[] args)
    {
        if (!RequireAdmin()) return 1;

        if (args.Length < 2 || !long.TryParse(args[1], out var userId) || userId == 0)
        {
            Console.Error.WriteLine("사용법: TimeBlocker.Service.exe set-admin <TelegramUserId>");
            Console.Error.WriteLine("User ID 는 숫자입니다. (@userinfobot 으로 확인할 수 있습니다)");
            return 1;
        }

        var store = new JsonConfigurationStore(AppPaths.ConfigFile);
        var config = store.Current;

        config.Telegram.AllowedUserIds = new List<long> { userId };
        store.Save(config);

        Console.WriteLine($"관리자 Telegram User ID 를 설정했습니다: {userId}");
        return 0;
    }

    private static int SetTelegramEnabled(bool enabled)
    {
        if (!RequireAdmin()) return 1;

        var store = new JsonConfigurationStore(AppPaths.ConfigFile);
        var config = store.Current;
        config.Telegram.Enabled = enabled;
        store.Save(config);

        Console.WriteLine($"Telegram 원격 제어: {(enabled ? "사용" : "사용 안 함")}");

        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(config.Telegram.ProtectedBotToken))
            {
                Console.WriteLine("경고: Bot Token 이 아직 설정되지 않았습니다. set-token 을 먼저 실행하세요.");
            }
            if (config.Telegram.AllowedUserIds.Count == 0)
            {
                Console.WriteLine("경고: 허용된 User ID 가 없습니다. set-admin 을 먼저 실행하세요.");
            }
        }

        return 0;
    }

    private static int ShowConfig()
    {
        var store = new JsonConfigurationStore(AppPaths.ConfigFile);
        var config = store.Current;

        Console.WriteLine($"설정파일: {AppPaths.ConfigFile}");
        Console.WriteLine();
        Console.WriteLine("[Schedule]");
        foreach (var day in config.Schedule.Days.OrderBy(d => ((int)d.Day + 6) % 7))
        {
            Console.WriteLine($"  {day.Day,-10} {day}");
        }

        Console.WriteLine();
        Console.WriteLine("[Targets]");
        foreach (var target in BlockTargets.Real)
        {
            var settings = config.GetTarget(target);
            Console.WriteLine(
                $"  {target.ToDisplayName(),-8} Mode={settings.Mode.ToCommandName(),-8} DNS={settings.UseDnsBlocking} " +
                $"Firewall={settings.UseFirewallBlocking} Domains={settings.Domains.Count} " +
                $"BrowserPolicy={settings.UseBrowserPolicyBlocking} Urls={settings.BlockedUrlPatterns.Count}");
        }

        Console.WriteLine();
        Console.WriteLine("[DNS]");
        Console.WriteLine($"  Enabled={config.Dns.Enabled} Mode={config.Dns.Mode} Port={config.Dns.ProxyPort}");
        Console.WriteLine($"  Upstream={string.Join(", ", config.Dns.UpstreamServers)}");

        Console.WriteLine();
        Console.WriteLine("[BrowserPolicy]");
        Console.WriteLine($"  Enabled={config.BrowserPolicy.Enabled}");
        Console.WriteLine($"  Browsers={string.Join(", ", config.BrowserPolicy.Browsers)}");
        Console.WriteLine(
            $"  DisableIncognito={config.BrowserPolicy.DisableIncognito} " +
            $"DisableGuestMode={config.BrowserPolicy.DisableGuestMode} " +
            $"DisableDnsOverHttps={config.BrowserPolicy.DisableDnsOverHttps}");
        Console.WriteLine($"  BlockExtensionInstalls={config.BrowserPolicy.BlockExtensionInstalls}");
        foreach (var pattern in config.Shorts.BlockedUrlPatterns)
        {
            Console.WriteLine($"  blocked url: {pattern}");
        }

        Console.WriteLine();
        Console.WriteLine("[Telegram]");
        Console.WriteLine($"  Enabled={config.Telegram.Enabled}");
        Console.WriteLine($"  BotToken={(string.IsNullOrWhiteSpace(config.Telegram.ProtectedBotToken) ? "(not set)" : "(configured, encrypted)")}");
        Console.WriteLine($"  AllowedUserIds={string.Join(", ", config.Telegram.AllowedUserIds)}");

        Console.WriteLine();
        Console.WriteLine("[TemporaryPermit]");
        Console.WriteLine($"  MaxMinutes={config.TemporaryPermit.MaxMinutes}");

        return 0;
    }

    /// <summary>
    /// 프로그램 제거 전에 시스템을 원래대로 되돌린다.
    /// uninstall 스크립트와 동일한 공통 로직(SystemRestoreService)을 사용한다.
    /// </summary>
    private static async Task<int> CleanupAsync(bool removeStateFiles)
    {
        if (!RequireAdmin()) return 1;

        var restore = CreateRestoreService();
        var report = await restore.RestoreAsync(removeStateFiles, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine(report.Format());
        Console.WriteLine();
        Console.WriteLine($"현재 어댑터 DNS: {CreateAdapterConfigurator().DescribeCurrentAdapterDns()}");

        // 일부라도 실패하면 0이 아닌 코드를 돌려줘서 스크립트가 감지할 수 있게 한다.
        return report.AllSucceeded ? 0 : 2;
    }

    /// <summary>doctor 점검을 수행하고 결과를 출력한다.</summary>
    private static async Task<int> DoctorAsync()
    {
        // doctor 는 관리자 권한이 없어도 최대한 점검한다. (권한 항목이 WARN 으로 표시된다)
        var store = new JsonConfigurationStore(AppPaths.ConfigFile);
        var logger = NullLogger.Instance;

        var doctor = new Maintenance.DoctorService(
            store,
            new HostsFileManager(NullLogger<HostsFileManager>.Instance),
            new FirewallManager(NullLogger<FirewallManager>.Instance),
            CreateAdapterConfigurator(),
            new Blocking.RobloxLocator(NullLogger<Blocking.RobloxLocator>.Instance),
            new RegistryPolicyEditor(NullLogger<RegistryPolicyEditor>.Instance),
            logger);

        var report = await doctor.RunAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(report.Format());

        return report.Overall switch
        {
            Shared.Models.DoctorStatus.Pass => 0,
            Shared.Models.DoctorStatus.Warn => 0,
            _ => 2
        };
    }

    private static Maintenance.ISystemRestoreService CreateRestoreService() =>
        new Maintenance.SystemRestoreService(
            new HostsFileManager(NullLogger<HostsFileManager>.Instance),
            new FirewallManager(NullLogger<FirewallManager>.Instance),
            CreateAdapterConfigurator(),
            new Blocking.DnsCacheFlusher(NullLogger<Blocking.DnsCacheFlusher>.Instance),
            CreateBrowserPolicyManager(),
            NullLogger.Instance);

    /// <summary>
    /// 서비스 밖(cleanup / doctor)에서도 브라우저 정책을 되돌릴 수 있어야 한다.
    /// DI 없이 직접 조립한다.
    /// </summary>
    private static IBrowserPolicyManager CreateBrowserPolicyManager() =>
        new BrowserPolicyManager(
            new RegistryPolicyEditor(NullLogger<RegistryPolicyEditor>.Instance),
            new BrowserPolicyStateStore(NullLogger<BrowserPolicyStateStore>.Instance),
            NullLogger<BrowserPolicyManager>.Instance);

    /// <summary>
    /// `dns-restore` 명령. 어댑터 DNS 만 즉시 원래대로 되돌린다.
    /// 서비스가 멈춰 있어 Telegram/CLI 를 쓸 수 없을 때의 최후 수단이다.
    /// </summary>
    private static async Task<int> RestoreDnsAsync()
    {
        if (!RequireAdmin()) return 1;

        var adapters = CreateAdapterConfigurator();

        if (!adapters.HasSavedOriginal)
        {
            Console.WriteLine("저장된 원래 DNS 설정이 없습니다. 어댑터는 변경되지 않은 상태입니다.");
            Console.WriteLine($"현재 어댑터 DNS: {adapters.DescribeCurrentAdapterDns()}");
            return 0;
        }

        var names = string.Join(", ", adapters.ConfiguredAdapterNames);
        var restored = await adapters.RestoreOriginalAsync(CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine($"어댑터 {restored}개의 DNS 를 원래 설정으로 되돌렸습니다. ({names})");
        Console.WriteLine($"현재 어댑터 DNS: {adapters.DescribeCurrentAdapterDns()}");
        return 0;
    }

    private static Blocking.Dns.NetworkAdapterDnsConfigurator CreateAdapterConfigurator()
    {
        var stateStore = new Blocking.Dns.AdapterDnsStateStore(
            NullLogger<Blocking.Dns.AdapterDnsStateStore>.Instance);

        return new Blocking.Dns.NetworkAdapterDnsConfigurator(
            NullLogger<Blocking.Dns.NetworkAdapterDnsConfigurator>.Instance, stateStore);
    }
}
