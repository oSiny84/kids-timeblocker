using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Blocking;

public interface IExecutableLocator
{
    /// <summary>주어진 실행파일 이름들을 시스템에서 찾아 전체 경로 목록을 돌려준다.</summary>
    IReadOnlyList<string> Locate(IReadOnlyCollection<string> processNames);
}

/// <summary>
/// Roblox 등 사용자별로 설치 경로가 달라지는 실행파일을 자동 탐색한다.
///
/// 서비스는 LocalSystem 으로 돌기 때문에 %LOCALAPPDATA% 를 그대로 쓰면
/// C:\Windows\system32\config\systemprofile\... 을 보게 된다.
/// 따라서 C:\Users\*\AppData\Local\Roblox\Versions\* 를 직접 훑는다.
/// </summary>
public sealed class RobloxLocator : IExecutableLocator
{
    private readonly ILogger<RobloxLocator> _logger;

    // 탐색 결과를 짧게 캐시한다. (매 10초마다 디스크를 훑지 않기 위해)
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();
    private DateTime _cachedAt = DateTime.MinValue;
    private string _cacheKey = string.Empty;
    private IReadOnlyList<string> _cached = Array.Empty<string>();

    public RobloxLocator(ILogger<RobloxLocator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> Locate(IReadOnlyCollection<string> processNames)
    {
        if (processNames.Count == 0) return Array.Empty<string>();

        var key = string.Join('|', processNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        lock (_lock)
        {
            if (_cacheKey == key && DateTime.UtcNow - _cachedAt < _cacheLifetime) return _cached;
        }

        var found = Scan(processNames);

        lock (_lock)
        {
            _cacheKey = key;
            _cached = found;
            _cachedAt = DateTime.UtcNow;
        }

        return found;
    }

    /// <summary>캐시를 버린다. (설정이 바뀌었거나 Roblox 가 새로 설치된 경우)</summary>
    public void InvalidateCache()
    {
        lock (_lock) _cachedAt = DateTime.MinValue;
    }

    private IReadOnlyList<string> Scan(IReadOnlyCollection<string> processNames)
    {
        var wanted = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateSearchRoots())
        {
            ScanDirectory(root, wanted, results, depth: 0, maxDepth: 3);
        }

        if (results.Count > 0)
        {
            _logger.LogDebug("실행파일 {Count}개를 찾았습니다: {Paths}", results.Count, string.Join(", ", results));
        }

        return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Roblox 가 설치될 수 있는 위치들.</summary>
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
}
