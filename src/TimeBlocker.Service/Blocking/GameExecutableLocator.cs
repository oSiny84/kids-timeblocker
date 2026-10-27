using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Blocking;

public interface IExecutableLocator
{
    /// <summary>주어진 실행파일 이름들을 시스템에서 찾아 전체 경로 목록을 돌려준다.</summary>
    IReadOnlyList<string> Locate(IReadOnlyCollection<string> processNames);
}

/// <summary>
/// Roblox / 서든어택 등 사용자마다 설치 경로가 달라지는 게임 실행파일을 찾는다.
/// 방화벽 규칙은 실행파일 경로로 만들기 때문에, 못 찾으면 규칙이 아예 생기지 않는다.
///
/// 두 길로 찾는다.
///   1) 알려진 설치 위치를 디스크에서 훑는다.
///      서비스는 LocalSystem 으로 돌기 때문에 %LOCALAPPDATA% 를 그대로 쓰면
///      C:\Windows\system32\config\systemprofile\... 을 보게 된다.
///      따라서 C:\Users\*\AppData\Local\Roblox\Versions\* 같은 곳을 직접 훑는다.
///   2) 지금 실행 중인 프로세스의 경로를 읽는다. 한 번 본 경로는 서비스가 도는 동안 기억한다.
///      넥슨 게임은 설치 폴더를 사용자가 고를 수 있어서 1) 만으로는 놓칠 수 있다.
///      놓치면 프로세스를 종료해도 다시 켤 때마다 유예 시간을 새로 받아 계속 할 수 있다.
///      차단 시간에 한 번이라도 실행되면 그 경로로 규칙이 생기므로 다시 켜도 접속이 안 된다.
/// </summary>
public sealed class GameExecutableLocator : IExecutableLocator
{
    private readonly ILogger<GameExecutableLocator> _logger;
    private readonly Func<IEnumerable<string>> _searchRoots;
    private readonly Func<IReadOnlyCollection<string>, IReadOnlyList<string>> _runningPaths;

    // 탐색 결과를 짧게 캐시한다. (매 10초마다 디스크를 훑지 않기 위해)
    // 대상마다 찾는 이름이 다르므로 이름 묶음별로 따로 둔다. 하나만 두면 Roblox 와
    // 서든어택이 번갈아 덮어써서 결국 매 주기 디스크를 훑게 된다.
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<string> Paths)> _cache = new(StringComparer.Ordinal);

    /// <summary>실행 중인 프로세스에서 알아낸 경로. 프로세스가 꺼져도 잊지 않는다.</summary>
    private readonly HashSet<string> _learned = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="searchRoots">테스트용. 디스크 탐색 위치를 바꿔 끼운다.</param>
    /// <param name="runningPaths">테스트용. 실행 중인 프로세스 경로 조회를 바꿔 끼운다.</param>
    public GameExecutableLocator(
        ILogger<GameExecutableLocator> logger,
        Func<IEnumerable<string>>? searchRoots = null,
        Func<IReadOnlyCollection<string>, IReadOnlyList<string>>? runningPaths = null)
    {
        _logger = logger;
        _searchRoots = searchRoots ?? EnumerateSearchRoots;
        _runningPaths = runningPaths ?? ReadRunningPaths;
    }

    public IReadOnlyList<string> Locate(IReadOnlyCollection<string> processNames)
    {
        if (processNames.Count == 0) return Array.Empty<string>();

        var wanted = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var onDisk = LocateOnDisk(processNames, wanted);
        var learned = LearnFromRunning(processNames, wanted);

        if (learned.Count == 0) return onDisk;

        return onDisk
            .Concat(learned)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>디스크 탐색 캐시를 버린다. (설정이 바뀌었거나 게임이 새로 설치된 경우)</summary>
    public void InvalidateCache()
    {
        lock (_lock) _cache.Clear();
    }

    private IReadOnlyList<string> LocateOnDisk(IReadOnlyCollection<string> processNames, HashSet<string> wanted)
    {
        var key = string.Join('|', processNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.At < _cacheLifetime) return entry.Paths;
        }

        var found = Scan(wanted);

        lock (_lock)
        {
            _cache[key] = (DateTime.UtcNow, found);
        }

        return found;
    }

    /// <summary>
    /// 지금 돌고 있는 프로세스의 경로를 기억해 두고, 이 이름들에 해당하는 기억된 경로를 돌려준다.
    /// 프로세스 조회는 가벼우므로 캐시하지 않는다. 켜자마자 다음 주기에 규칙이 생겨야 한다.
    /// </summary>
    private IReadOnlyList<string> LearnFromRunning(IReadOnlyCollection<string> processNames, HashSet<string> wanted)
    {
        IReadOnlyList<string> running;
        try
        {
            running = _runningPaths(processNames);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "실행 중인 프로세스 경로 조회 실패");
            running = Array.Empty<string>();
        }

        List<string> remembered;
        lock (_lock)
        {
            foreach (var path in running)
            {
                if (!wanted.Contains(Path.GetFileName(path))) continue;

                if (_learned.Add(path))
                {
                    _logger.LogInformation("실행 중인 프로세스에서 실행파일 경로를 알아냈습니다: {Path}", path);
                }
            }

            // 다른 대상의 이름으로 기억된 경로는 돌려주지 않는다.
            remembered = _learned.Where(p => wanted.Contains(Path.GetFileName(p))).ToList();
        }

        // 지워졌거나 옮겨진 경로로는 규칙을 만들 수 없다.
        return remembered.Where(File.Exists).ToList();
    }

    private IReadOnlyList<string> Scan(HashSet<string> wanted)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _searchRoots())
        {
            ScanDirectory(root, wanted, results, depth: 0, maxDepth: 3);
        }

        if (results.Count > 0)
        {
            _logger.LogDebug("실행파일 {Count}개를 찾았습니다: {Paths}", results.Count, string.Join(", ", results));
        }

        return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>게임이 설치될 수 있는 위치들.</summary>
    private IEnumerable<string> EnumerateSearchRoots()
    {
        var roots = new List<string>();

        // 1) 모든 사용자 프로필의 LocalAppData\Roblox\Versions
        try
        {
            var usersRoot = Path.Combine(
                Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\",
                "Users");

            if (Directory.Exists(usersRoot))
            {
                foreach (var profile in Directory.EnumerateDirectories(usersRoot))
                {
                    roots.Add(Path.Combine(profile, "AppData", "Local", "Roblox", "Versions"));
                    roots.Add(Path.Combine(profile, "AppData", "Local", "Bloxstrap", "Versions"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "사용자 프로필 목록을 읽지 못했습니다.");
        }

        // 2) Program Files (신규 Roblox 설치 방식)
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) roots.Add(Path.Combine(path, "Roblox"));
        }

        // 3) 넥슨 게임(서든어택 등)의 기본 위치는 C:\Nexon\<게임> 이다.
        //    런처에서 설치 폴더를 고를 수 있어서 드라이브만 바꾸는 경우가 많다.
        //    고정 드라이브마다 흔히 쓰는 위치를 본다. (Nexon\Library 는 탐색 깊이 안에 들어온다)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                var root = drive.RootDirectory.FullName;
                roots.Add(Path.Combine(root, "Nexon"));
                roots.Add(Path.Combine(root, "Games", "Nexon"));
                roots.Add(Path.Combine(root, "Program Files", "Nexon"));
                roots.Add(Path.Combine(root, "Program Files (x86)", "Nexon"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "드라이브 목록을 읽지 못했습니다.");
        }

        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>깊이 제한을 두고 재귀 탐색한다. (권한 없는 폴더는 조용히 건너뛴다)</summary>
    private void ScanDirectory(
        string directory,
        HashSet<string> wanted,
        HashSet<string> results,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.exe"))
            {
                if (wanted.Contains(Path.GetFileName(file))) results.Add(file);
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                ScanDirectory(sub, wanted, results, depth + 1, maxDepth);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 접근할 수 없는 폴더는 무시한다.
        }
        catch (DirectoryNotFoundException)
        {
            // 탐색 중 삭제된 폴더는 무시한다.
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "폴더 탐색 실패: {Directory}", directory);
        }
    }

    // ============================================================ 실행 중인 프로세스 경로

    private IReadOnlyList<string> ReadRunningPaths(IReadOnlyCollection<string> processNames)
    {
        var results = new List<string>();

        foreach (var name in processNames)
        {
            // Process.GetProcessesByName 은 확장자를 뺀 이름을 받는다.
            var bare = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(bare)) continue;

            Process[] found;
            try
            {
                found = Process.GetProcessesByName(bare);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "프로세스 조회 실패: {Name}", bare);
                continue;
            }

            foreach (var process in found)
            {
                try
                {
                    var path = QueryImagePath(process.Id);
                    if (!string.IsNullOrEmpty(path)) results.Add(path);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "프로세스 경로 조회 실패: {Name}", bare);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 프로세스 실행파일의 전체 경로.
    ///
    /// Process.MainModule 은 메모리 읽기 권한이 필요해서 안티치트가 지키는 게임
    /// (서든어택은 Easy Anti-Cheat)에서는 실패할 수 있다. QueryFullProcessImageName 은
    /// 제한된 조회 권한만으로 동작한다. 작업 관리자가 경로를 보여주는 것과 같은 방식이다.
    /// </summary>
    private static string? QueryImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
