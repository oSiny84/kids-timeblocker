using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Service.Blocking;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// 게임 실행파일 탐색.
/// 방화벽 규칙은 실행파일 경로로 만들기 때문에 여기서 못 찾으면 규칙이 아예 생기지 않는다.
/// </summary>
public class GameExecutableLocatorTests : IDisposable
{
    private static readonly string[] SuddenAttack = { "SuddenAttack.exe" };
    private static readonly string[] Roblox = { "RobloxPlayerBeta.exe" };

    private readonly string _directory;

    public GameExecutableLocatorTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 정리 실패는 무시 */ }
    }

    private string CreateExe(params string[] parts)
    {
        var path = Path.Combine(new[] { _directory }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private GameExecutableLocator CreateSut(
        Func<IEnumerable<string>>? roots = null,
        Func<IReadOnlyCollection<string>, IReadOnlyList<string>>? running = null) =>
        new(NullLogger<GameExecutableLocator>.Instance,
            roots ?? (() => new[] { _directory }),
            running ?? (_ => Array.Empty<string>()));

    [Fact]
    public void FindsSuddenAttackUnderNexonFolder()
    {
        // 넥슨 기본 설치 위치: C:\Nexon\SuddenAttack\SuddenAttack.exe
        var exe = CreateExe("Nexon", "SuddenAttack", "SuddenAttack.exe");
        CreateExe("Nexon", "SuddenAttack", "Uninstall.exe");

        var found = CreateSut().Locate(SuddenAttack);

        Assert.Equal(new[] { exe }, found);
    }

    [Fact]
    public void FindsSuddenAttackInsideNexonLauncherLibrary()
    {
        // 넥슨 런처 라이브러리 폴더는 한 단계 더 깊다: C:\Nexon\Library\SuddenAttack\...
        var exe = CreateExe("Nexon", "Library", "SuddenAttack", "SuddenAttack.exe");

        var found = CreateSut().Locate(SuddenAttack);

        Assert.Equal(new[] { exe }, found);
    }

    [Fact]
    public void DiskScanIsCachedPerTarget()
    {
        // 대상마다 이름이 다르다. 캐시가 하나뿐이면 Roblox 와 서든어택이 번갈아 덮어써서
        // 10초마다 디스크 전체를 다시 훑게 된다.
        CreateExe("Nexon", "SuddenAttack", "SuddenAttack.exe");
        CreateExe("Roblox", "Versions", "version-1", "RobloxPlayerBeta.exe");

        var scans = 0;
        var sut = CreateSut(roots: () =>
        {
            scans++;
            return new[] { _directory };
        });

        for (var i = 0; i < 3; i++)
        {
            Assert.Single(sut.Locate(SuddenAttack));
            Assert.Single(sut.Locate(Roblox));
        }

        Assert.Equal(2, scans);
    }

    [Fact]
    public void RemembersPathOfRunningGame_AfterItIsClosed()
    {
        // 기본 위치가 아닌 곳에 설치한 경우. 실행 중일 때 한 번 보면 꺼진 뒤에도 기억해야
        // 다시 켰을 때 방화벽이 접속을 막는다.
        var exe = CreateExe("Games", "SA", "SuddenAttack.exe");
        var running = new List<string> { exe };

        var sut = CreateSut(roots: Array.Empty<string>, running: _ => running.ToList());

        Assert.Equal(new[] { exe }, sut.Locate(SuddenAttack));

        running.Clear();
        Assert.Equal(new[] { exe }, sut.Locate(SuddenAttack));
    }

    [Fact]
    public void RememberedPath_IsOnlyReturnedForItsOwnTarget()
    {
        var exe = CreateExe("Games", "SA", "SuddenAttack.exe");
        var sut = CreateSut(roots: Array.Empty<string>, running: _ => new[] { exe });

        sut.Locate(SuddenAttack);

        Assert.Empty(sut.Locate(Roblox));
    }

    [Fact]
    public void RememberedPath_IsDroppedWhenFileIsGone()
    {
        // 지워졌거나 옮겨진 파일로는 방화벽 규칙을 만들 수 없다.
        var exe = CreateExe("Games", "SA", "SuddenAttack.exe");
        var reported = true;
        var sut = CreateSut(roots: Array.Empty<string>, running: _ => reported ? new[] { exe } : Array.Empty<string>());

        Assert.Single(sut.Locate(SuddenAttack));

        reported = false;
        File.Delete(exe);

        Assert.Empty(sut.Locate(SuddenAttack));
    }

    [Fact]
    public void DiskAndRunningResults_AreMergedWithoutDuplicates()
    {
        var installed = CreateExe("Nexon", "SuddenAttack", "SuddenAttack.exe");
        var elsewhere = CreateExe("Games", "SA2", "SuddenAttack.exe");

        var sut = CreateSut(
            roots: () => new[] { Path.Combine(_directory, "Nexon") },
            running: _ => new[] { installed, elsewhere });

        var found = sut.Locate(SuddenAttack);

        Assert.Equal(2, found.Count);
        Assert.Contains(installed, found);
        Assert.Contains(elsewhere, found);
    }

    [Fact]
    public void FailingProcessLookup_StillReturnsDiskResults()
    {
        var exe = CreateExe("Nexon", "SuddenAttack", "SuddenAttack.exe");
        var sut = CreateSut(running: _ => throw new InvalidOperationException("access denied"));

        Assert.Equal(new[] { exe }, sut.Locate(SuddenAttack));
    }
}
