using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TimeBlocker.Service.Blocking;

/// <summary>실행 중인 프로세스 하나.</summary>
public readonly record struct RunningProcess(int Id, string Name);

public interface IRunningProcessTracker
{
    /// <summary>주어진 실행파일 이름으로 돌고 있는 프로세스를 찾는다.</summary>
    IReadOnlyList<RunningProcess> Find(IReadOnlyCollection<string> processNames);

    /// <summary>프로세스를 종료한다. 성공 여부를 돌려준다.</summary>
    bool TryTerminate(RunningProcess process);
}

/// <summary>
/// System.Diagnostics.Process 로 조회/종료한다.
/// 숨겨진 방식이나 커널 후킹을 쓰지 않고, 관리자 권한으로 할 수 있는 일만 한다.
/// </summary>
public sealed class RunningProcessTracker : IRunningProcessTracker
{
    private readonly ILogger<RunningProcessTracker> _logger;

    public RunningProcessTracker(ILogger<RunningProcessTracker> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<RunningProcess> Find(IReadOnlyCollection<string> processNames)
    {
        var results = new List<RunningProcess>();

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
                    results.Add(new RunningProcess(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException)
                {
                    // 조회 도중 종료된 프로세스는 무시한다.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return results;
    }

    public bool TryTerminate(RunningProcess process)
    {
        try
        {
            using var handle = Process.GetProcessById(process.Id);
            handle.Kill(entireProcessTree: true);
            _logger.LogInformation("프로세스를 종료했습니다: {Name} (PID {Pid})", process.Name, process.Id);
            return true;
        }
        catch (ArgumentException)
        {
            // 이미 종료됐다. 목적은 달성된 것이므로 성공으로 본다.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "프로세스 종료 실패: {Name} (PID {Pid})", process.Name, process.Id);
            return false;
        }
    }
}
