using Microsoft.Extensions.Logging.Abstractions;
using TimeBlocker.Service.Blocking;
using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Models;
using Xunit;

namespace TimeBlocker.Tests;

/// <summary>
/// "차단 시간인데 게임이 돌고 있다" 상황의 유예/경고/종료 흐름을 검증한다.
/// 실제 프로세스를 죽이지 않도록 조회/종료를 가짜로 바꿔 넣는다.
/// </summary>
public class ProcessEnforcerTests
{
    private readonly FakeProcessTracker _processes = new();
    private readonly FakeNotifier _notifier = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 9, 22, 21, 0, 0, DateTimeKind.Local));
    private readonly TimeBlockerConfig _config = TimeBlockerConfig.CreateDefault();
    private readonly ProcessEnforcer _sut;

    public ProcessEnforcerTests()
    {
        _config.Normalize();
        _sut = new ProcessEnforcer(_processes, _notifier, _clock, NullLogger<ProcessEnforcer>.Instance);
    }

    private static AccessDecision Blocked(BlockTarget target) =>
        new() { Target = target, IsBlocked = true, Reason = AccessReason.InBlockingSchedule };

    private static AccessDecision Allowed(BlockTarget target) =>
        new() { Target = target, IsBlocked = false, Reason = AccessReason.TemporaryPermit };

    private void Evaluate(params AccessDecision[] decisions) => _sut.Evaluate(decisions, _config);

    private void RobloxIsRunning() => _processes.Running.Add(new RunningProcess(1234, "RobloxPlayerBeta"));

    [Fact]
    public void BlockedAndRunning_WarnsButDoesNotKillImmediately()
    {
        RobloxIsRunning();

        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Empty(_processes.Killed);
        Assert.Single(_notifier.Messages);
        Assert.Contains("5분 뒤", _notifier.Messages[0]);
        Assert.Contains("21:05", _notifier.Messages[0]);
    }

    [Fact]
    public void AfterGracePeriod_ProcessIsTerminated()
    {
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));

        _clock.Advance(TimeSpan.FromMinutes(5));
        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Single(_processes.Killed);
        Assert.Equal(1234, _processes.Killed[0].Id);
    }

    [Fact]
    public void BeforeGracePeriod_NothingIsKilled()
    {
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));

        // 4분 59초 경과. 아직 종료하면 안 된다.
        _clock.Advance(TimeSpan.FromSeconds(299));
        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void FinalWarning_IsShownOnceAtOneMinuteLeft()
    {
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));

        _clock.Advance(TimeSpan.FromMinutes(4));
        Evaluate(Blocked(BlockTarget.Roblox));
        Evaluate(Blocked(BlockTarget.Roblox));  // 같은 주기가 여러 번 돌아도
        Evaluate(Blocked(BlockTarget.Roblox));  // 경고는 한 번만

        var finals = _notifier.Messages.Count(m => m.Contains("1분 뒤 종료"));
        Assert.Equal(1, finals);
    }

    [Fact]
    public void PermitDuringGrace_CancelsTermination()
    {
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));

        // 유예 중에 부모가 허용해 주면 예약이 사라져야 한다.
        _clock.Advance(TimeSpan.FromMinutes(2));
        Evaluate(Allowed(BlockTarget.Roblox));

        _clock.Advance(TimeSpan.FromMinutes(10));
        Evaluate(Allowed(BlockTarget.Roblox));

        Assert.Empty(_processes.Killed);
        Assert.Empty(_sut.Pending);
    }

    [Fact]
    public void ReblockedAfterPermit_GetsFullGracePeriodAgain()
    {
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));

        _clock.Advance(TimeSpan.FromMinutes(4));
        Evaluate(Allowed(BlockTarget.Roblox));   // 허용 -> 예약 취소

        Evaluate(Blocked(BlockTarget.Roblox));   // 다시 차단 -> 유예 재시작
        _clock.Advance(TimeSpan.FromMinutes(4));
        Evaluate(Blocked(BlockTarget.Roblox));

        // 처음 예약 기준으로는 8분이 지났지만, 재시작했으므로 아직 죽으면 안 된다.
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void ProcessClosedByUser_CancelsTermination()
    {
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));

        _processes.Running.Clear();   // 아이가 스스로 껐다
        _clock.Advance(TimeSpan.FromMinutes(2));
        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Empty(_sut.Pending);

        // 다시 켜면 유예가 처음부터 주어진다.
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));
        _clock.Advance(TimeSpan.FromMinutes(4));
        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public void NotRunning_DoesNotWarn()
    {
        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Empty(_notifier.Messages);
        Assert.Empty(_sut.Pending);
    }

    [Fact]
    public void TerminationDisabled_DoesNothing()
    {
        _config.Roblox.TerminateProcesses = false;
        RobloxIsRunning();

        Evaluate(Blocked(BlockTarget.Roblox));
        _clock.Advance(TimeSpan.FromMinutes(10));
        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Empty(_processes.Killed);
        Assert.Empty(_notifier.Messages);
    }

    [Fact]
    public void ZeroGrace_TerminatesOnNextTickWithoutWaiting()
    {
        _config.Enforcement.TerminationGraceMinutes = 0;
        RobloxIsRunning();

        Evaluate(Blocked(BlockTarget.Roblox));   // 예약 (마감 = 지금)
        Evaluate(Blocked(BlockTarget.Roblox));   // 다음 주기에 종료

        Assert.Single(_processes.Killed);
    }

    [Fact]
    public void YouTubeIsNotTerminatedByDefault()
    {
        // 브라우저를 통째로 죽이면 숙제하던 것까지 날아간다. 기본값은 꺼져 있어야 한다.
        Assert.False(_config.YouTube.TerminateProcesses);
    }

    [Fact]
    public void TerminationNotice_IsShownAfterKilling()
    {
        RobloxIsRunning();
        Evaluate(Blocked(BlockTarget.Roblox));

        _clock.Advance(TimeSpan.FromMinutes(5));
        Evaluate(Blocked(BlockTarget.Roblox));

        Assert.Contains(_notifier.Messages, m => m.Contains("종료했습니다"));
    }

    private sealed class FakeProcessTracker : IRunningProcessTracker
    {
        public List<RunningProcess> Running { get; } = new();
        public List<RunningProcess> Killed { get; } = new();

        public IReadOnlyList<RunningProcess> Find(IReadOnlyCollection<string> processNames) =>
            Running.ToList();

        public bool TryTerminate(RunningProcess process)
        {
            Killed.Add(process);
            Running.Remove(process);
            return true;
        }
    }

    private sealed class FakeNotifier : IUserSessionNotifier
    {
        public List<string> Messages { get; } = new();

        public int Notify(string title, string message)
        {
            Messages.Add(message);
            return 1;
        }
    }
}
