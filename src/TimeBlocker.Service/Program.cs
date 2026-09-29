using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Service.Blocking.Dns;
using TimeBlocker.Service.Ipc;
using TimeBlocker.Service.Logging;
using TimeBlocker.Service.Maintenance;
using TimeBlocker.Service.Remote;
using TimeBlocker.Service.Security;
using TimeBlocker.Service.Setup;
using TimeBlocker.Service.Worker;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;
using TimeBlocker.Shared.Remote;

namespace TimeBlocker.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AppPaths.EnsureDirectories();

        // 설치 직후 로컬에서만 수행하는 설정 명령들 (Telegram 토큰/관리자 ID 등).
        // 서비스로 실행될 때는 인자가 없으므로 그대로 호스트가 시작된다.
        if (args.Length > 0)
        {
            return await LocalSetupCommands.RunAsync(args).ConfigureAwait(false);
        }

        var builder = Host.CreateApplicationBuilder();

        // Windows Service 로도, 콘솔로도 실행할 수 있게 한다. (개발 중에는 콘솔이 편하다)
        builder.Services.AddWindowsService(options => options.ServiceName = "TimeBlocker");

        ConfigureLogging(builder);
        ConfigureServices(builder.Services);

        var host = builder.Build();

        // 데이터 폴더 권한을 정리한다. (일반 사용자 계정에서 설정 변경 방지)
        var startupLogger = host.Services.GetRequiredService<ILogger<Worker.EnforcementWorker>>();
        DataDirectoryHardener.Harden(AppPaths.RootDirectory, startupLogger);

        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            startupLogger.LogCritical(ex, "서비스가 예기치 않게 종료되었습니다.");
            return 1;
        }
        finally
        {
            // 로그 큐를 비우고 파일을 닫는다.
            host.Services.GetService<FileLogWriter>()?.Dispose();
        }
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        // 로그 설정은 설정파일에서 읽어야 하므로 여기서 한 번 로드한다.
        var bootstrapStore = new JsonConfigurationStore(AppPaths.ConfigFile);
        var logging = bootstrapStore.Current.Logging;

        var writer = new FileLogWriter(AppPaths.LogDirectory, logging.MaxFileSizeMb, logging.RetainDays);

        builder.Services.AddSingleton(writer);
        builder.Services.AddSingleton<IConfigurationStore>(bootstrapStore);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(writer, logging.MinimumLevel));
        builder.Logging.SetMinimumLevel(FileLoggerProvider.ParseLevel(logging.MinimumLevel));

        // 콘솔로 직접 실행하는 경우에는 화면에도 뿌려준다.
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISystemClock>(SystemClock.Instance);

        // --- 핵심 로직 ---
        services.AddSingleton<IScheduleManager, ScheduleManager>();

        services.AddSingleton<IPermitStateStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JsonPermitStateStore>>();
            return new JsonPermitStateStore(
                AppPaths.PermitStateFile,
                (message, ex) => logger.LogError(ex, "{Message}", message));
        });

        services.AddSingleton<ITemporaryPermitManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TemporaryPermitManager>>();
            return new TemporaryPermitManager(
                sp.GetRequiredService<IPermitStateStore>(),
                sp.GetRequiredService<ISystemClock>(),
                () => sp.GetRequiredService<IConfigurationStore>().Current.TemporaryPermit,
                message => logger.LogInformation("{Message}", message));
        });

        services.AddSingleton<IAccessPolicyEngine>(sp => new AccessPolicyEngine(
            () => sp.GetRequiredService<IConfigurationStore>().Current,
            sp.GetRequiredService<IScheduleManager>(),
            sp.GetRequiredService<ITemporaryPermitManager>(),
            sp.GetRequiredService<ISystemClock>()));

        // --- 차단 구현 ---
        services.AddSingleton<IHostsFileManager>(sp =>
            new HostsFileManager(sp.GetRequiredService<ILogger<HostsFileManager>>()));
        services.AddSingleton<IFirewallManager, FirewallManager>();
        services.AddSingleton<IDnsProxyServer, DnsProxyServer>();
        services.AddSingleton<IDnsSelfTest, DnsSelfTest>();
        services.AddSingleton<IAdapterDnsStateStore>(sp =>
            new AdapterDnsStateStore(sp.GetRequiredService<ILogger<AdapterDnsStateStore>>()));
        services.AddSingleton<INetworkAdapterDnsConfigurator, NetworkAdapterDnsConfigurator>();
        services.AddSingleton<IDnsCacheFlusher, DnsCacheFlusher>();
        services.AddSingleton<RobloxLocator>();
        services.AddSingleton<IUserSessionNotifier, UserSessionNotifier>();
        services.AddSingleton<IRunningProcessTracker, RunningProcessTracker>();
        services.AddSingleton<ProcessEnforcer>();
        services.AddSingleton<IUserMessenger, UserMessenger>();

        // --- 진단 / 원상복구 (doctor, cleanup 공통) ---
        services.AddSingleton<IDiagnosticsService>(sp => new DoctorService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IHostsFileManager>(),
            sp.GetRequiredService<IFirewallManager>(),
            sp.GetRequiredService<INetworkAdapterDnsConfigurator>(),
            sp.GetRequiredService<RobloxLocator>(),
            sp.GetRequiredService<ILogger<DoctorService>>()));

        services.AddSingleton<ISystemRestoreService>(sp => new SystemRestoreService(
            sp.GetRequiredService<IHostsFileManager>(),
            sp.GetRequiredService<IFirewallManager>(),
            sp.GetRequiredService<INetworkAdapterDnsConfigurator>(),
            sp.GetRequiredService<IDnsCacheFlusher>(),
            sp.GetRequiredService<ILogger<SystemRestoreService>>()));

        services.AddSingleton<BlockingCoordinator>();
        services.AddSingleton<IEnforcementController>(sp => sp.GetRequiredService<BlockingCoordinator>());

        // --- 명령 처리 (Telegram / CLI 공통) ---
        services.AddSingleton<IRemoteCommandParser, RemoteCommandParser>();
        services.AddSingleton<IRemoteCommandHandler>(sp => new RemoteCommandHandler(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<ITemporaryPermitManager>(),
            sp.GetRequiredService<IAccessPolicyEngine>(),
            sp.GetRequiredService<IScheduleManager>(),
            sp.GetRequiredService<IEnforcementController>(),
            sp.GetRequiredService<ISystemClock>(),
            sp.GetRequiredService<IRemoteCommandParser>(),
            sp.GetRequiredService<ILogger<RemoteCommandHandler>>(),
            sp.GetRequiredService<IDiagnosticsService>(),
            sp.GetRequiredService<IUserMessenger>()));

        // --- 원격 채널 ---
        services.AddSingleton<IHttpClientFactoryLite, SharedHttpClientFactory>();
        services.AddSingleton<IRemoteCommandProvider, TelegramRemoteCommandProvider>();

        // --- 백그라운드 작업 ---
        services.AddHostedService<EnforcementWorker>();
        services.AddHostedService<RemoteProviderWorker>();
        services.AddHostedService<NamedPipeIpcServer>();
    }
}
