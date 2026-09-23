namespace TimeBlocker.Shared.Configuration;

/// <summary>
/// 서비스와 GUI 가 동일한 위치를 보도록 경로를 한 곳에서 계산한다.
/// 기본 루트: %ProgramData%\TimeBlocker
/// (LocalSystem 으로 도는 서비스와 로그인 사용자 GUI 가 함께 접근할 수 있는 위치)
/// </summary>
public static class AppPaths
{
    public const string PipeName = "TimeBlocker.Service";

    /// <summary>환경변수 TIMEBLOCKER_DATA 로 덮어쓸 수 있다. (테스트/개발용)</summary>
    public static string RootDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("TIMEBLOCKER_DATA");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "TimeBlocker");
        }
    }

    public static string ConfigFile => Path.Combine(RootDirectory, "timeblocker.config.json");

    public static string PermitStateFile => Path.Combine(RootDirectory, "state", "permits.json");

    public static string RuntimeStateFile => Path.Combine(RootDirectory, "state", "runtime.json");

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string HostsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(Path.Combine(RootDirectory, "state"));
        Directory.CreateDirectory(LogDirectory);
    }
}
