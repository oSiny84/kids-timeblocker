using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;
using Xunit;

namespace TimeBlocker.Tests;

public class AccessPolicyEngineTests
{
    private static DateTime At(int hour, int minute) => new(2026, 9, 22, hour, minute, 0, DateTimeKind.Local);

    private sealed class Harness
    {
        public required TimeBlockerConfig Config { get; init; }
        public required FixedClock Clock { get; init; }
        public required TemporaryPermitManager Permits { get; init; }
        public required AccessPolicyEngine Engine { get; init; }
    }

    private static Harness CreateHarness(DateTime now)
    {
        var config = TimeBlockerConfig.CreateDefault();
        // 테스트를 단순하게 하기 위해 모든 요일 21:00~07:00 으로 통일한다.
        foreach (var day in config.Schedule.Days)
        {
            day.Enabled = true;
            day.Start = "21:00";
            day.End = "07:00";
        }
        config.Normalize();

        var clock = new FixedClock(now);
        var permits = new TemporaryPermitManager(
            new InMemoryPermitStateStore(), clock, () => config.TemporaryPermit);

        var engine = new AccessPolicyEngine(() => config, new ScheduleManager(), permits, clock);

        return new Harness { Config = config, Clock = clock, Permits = permits, Engine = engine };
    }

    [Fact]
    public void OutsideSchedule_IsAllowed()
    {
        var h = CreateHarness(At(20, 59));

        var decision = h.Engine.Evaluate(BlockTarget.YouTube);

        Assert.False(decision.IsBlocked);
        Assert.Equal(AccessReason.OutsideSchedule, decision.Reason);
    }

    [Fact]
    public void InsideSchedule_IsBlocked()
    {
        var h = CreateHarness(At(21, 0));

        var decision = h.Engine.Evaluate(BlockTarget.YouTube);

        Assert.True(decision.IsBlocked);
        Assert.Equal(AccessReason.InBlockingSchedule, decision.Reason);
    }

    [Fact]
    public void OpenMode_BeatsSchedule()
    {
        var h = CreateHarness(At(23, 0));
        h.Config.YouTube.Mode = BlockMode.Open;

        var youtube = h.Engine.Evaluate(BlockTarget.YouTube);
        var roblox = h.Engine.Evaluate(BlockTarget.Roblox);

        Assert.False(youtube.IsBlocked);
        Assert.Equal(AccessReason.AlwaysOpen, youtube.Reason);

        // Roblox 는 그대로 차단되어야 한다.
        Assert.True(roblox.IsBlocked);
    }

    [Fact]
    public void BlockedMode_BeatsSchedule()
    {
        // 차단 시간대가 아닌 낮 시간인데도 막혀야 한다.
        var h = CreateHarness(At(15, 0));
        h.Config.YouTube.Mode = BlockMode.Blocked;

        var youtube = h.Engine.Evaluate(BlockTarget.YouTube);
        var roblox = h.Engine.Evaluate(BlockTarget.Roblox);

        Assert.True(youtube.IsBlocked);
        Assert.Equal(AccessReason.AlwaysBlocked, youtube.Reason);

        // 스케줄대로인 Roblox 는 낮에 열려 있어야 한다.
        Assert.False(roblox.IsBlocked);
    }

    [Fact]
    public void TemporaryPermit_BeatsBlockedMode()
    {
        // block 으로 잠가둔 상태에서도 "30분만 열어줘" 는 되어야 한다.
        var h = CreateHarness(At(23, 0));
        h.Config.YouTube.Mode = BlockMode.Blocked;
        h.Permits.Grant(BlockTarget.YouTube, 30, "test");

        var youtube = h.Engine.Evaluate(BlockTarget.YouTube);

        Assert.False(youtube.IsBlocked);
        Assert.Equal(AccessReason.TemporaryPermit, youtube.Reason);
    }

    [Fact]
    public void TemporaryPermit_BeatsSchedule_ThenReBlocksAutomatically()
    {
        var h = CreateHarness(At(21, 10));

        Assert.True(h.Engine.Evaluate(BlockTarget.YouTube).IsBlocked);

        h.Permits.Grant(BlockTarget.YouTube, 30, "test"); // -> 21:40

        h.Clock.SetLocal(At(21, 20));
        var allowed = h.Engine.Evaluate(BlockTarget.YouTube);
        Assert.False(allowed.IsBlocked);
        Assert.Equal(AccessReason.TemporaryPermit, allowed.Reason);
        Assert.NotNull(allowed.PermitExpiresUtc);

        h.Clock.SetLocal(At(21, 39));
        Assert.False(h.Engine.Evaluate(BlockTarget.YouTube).IsBlocked);

        // 아무 조작 없이 만료 시각에 다시 차단
        h.Clock.SetLocal(At(21, 40));
        var reBlocked = h.Engine.Evaluate(BlockTarget.YouTube);
        Assert.True(reBlocked.IsBlocked);
        Assert.Equal(AccessReason.InBlockingSchedule, reBlocked.Reason);
    }

    [Fact]
    public void YouTubePermit_DoesNotUnblockRoblox()
    {
        var h = CreateHarness(At(22, 0));
        h.Permits.Grant(BlockTarget.YouTube, 30, "test");

        Assert.False(h.Engine.Evaluate(BlockTarget.YouTube).IsBlocked);
        Assert.True(h.Engine.Evaluate(BlockTarget.Roblox).IsBlocked);
    }

    [Fact]
    public void AllPermit_UnblocksEverything()
    {
        var h = CreateHarness(At(22, 0));
        h.Permits.Grant(BlockTarget.All, 20, "test");

        foreach (var decision in h.Engine.EvaluateAll())
        {
            Assert.False(decision.IsBlocked);
            Assert.Equal(AccessReason.TemporaryPermit, decision.Reason);
        }
    }

    [Fact]
    public void Lock_CancelsPermitImmediately()
    {
        var h = CreateHarness(At(22, 0));
        h.Permits.Grant(BlockTarget.All, 60, "test");
        Assert.False(h.Engine.Evaluate(BlockTarget.Roblox).IsBlocked);

        h.Permits.CancelAll("test");

        Assert.True(h.Engine.Evaluate(BlockTarget.Roblox).IsBlocked);
    }

    [Fact]
    public void PermitOutsideSchedule_StaysAllowedForScheduleReason()
    {
        // 허용 시간대에 permit 이 만료되어도 여전히 ALLOW 여야 한다.
        var h = CreateHarness(At(12, 0));
        h.Permits.Grant(BlockTarget.YouTube, 10, "test");

        h.Clock.SetLocal(At(13, 0));
        var decision = h.Engine.Evaluate(BlockTarget.YouTube);

        Assert.False(decision.IsBlocked);
        Assert.Equal(AccessReason.OutsideSchedule, decision.Reason);
    }

    [Fact]
    public void EvaluateAll_CoversBothRealTargets()
    {
        var h = CreateHarness(At(23, 0));

        var decisions = h.Engine.EvaluateAll();

        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, d => d.Target == BlockTarget.YouTube);
        Assert.Contains(decisions, d => d.Target == BlockTarget.Roblox);
    }

    [Fact]
    public void Evaluate_All_Throws()
    {
        var h = CreateHarness(At(23, 0));
        Assert.Throws<ArgumentException>(() => h.Engine.Evaluate(BlockTarget.All));
    }
}
