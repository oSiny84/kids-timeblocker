using TimeBlocker.Shared.Common;
using TimeBlocker.Shared.Configuration;
using TimeBlocker.Shared.Core;
using TimeBlocker.Shared.Models;
using Xunit;

namespace TimeBlocker.Tests;

public class TemporaryPermitManagerTests
{
    private static readonly TemporaryPermitSettings Settings = new() { MaxMinutes = 120, MinMinutes = 1 };

    private static DateTime At(int hour, int minute) => new(2026, 9, 22, hour, minute, 0, DateTimeKind.Local);

    private static (TemporaryPermitManager manager, FixedClock clock, InMemoryPermitStateStore store) Create(
        DateTime start, TemporaryPermitSettings? settings = null)
    {
        var clock = new FixedClock(start);
        var store = new InMemoryPermitStateStore();
        var manager = new TemporaryPermitManager(store, clock, () => settings ?? Settings);
        return (manager, clock, store);
    }

    [Fact]
    public void Grant_ThenExpires_ExactlyAtExpireTime()
    {
        // 사양 예시: 21:10 에 /youtube 30 -> 21:39 허용, 21:40 차단
        var (manager, clock, _) = Create(At(21, 10));

        var result = manager.Grant(BlockTarget.YouTube, 30, "test");
        Assert.True(result.Success);

        clock.SetLocal(At(21, 20));
        Assert.NotNull(manager.GetEffective(BlockTarget.YouTube));

        clock.SetLocal(At(21, 39));
        Assert.NotNull(manager.GetEffective(BlockTarget.YouTube));

        clock.SetLocal(At(21, 40));
        Assert.Null(manager.GetEffective(BlockTarget.YouTube));
    }

    [Fact]
    public void Grant_RejectsOverMaxMinutes()
    {
        var (manager, _, _) = Create(At(21, 0));

        var result = manager.Grant(BlockTarget.YouTube, 500, "test");

        Assert.False(result.Success);
        Assert.Contains("120", result.Message);
        Assert.Null(manager.GetEffective(BlockTarget.YouTube));
    }

    [Fact]
    public void Grant_RejectsZeroOrNegative()
    {
        var (manager, _, _) = Create(At(21, 0));

        Assert.False(manager.Grant(BlockTarget.Roblox, 0, "test").Success);
        Assert.False(manager.Grant(BlockTarget.Roblox, -10, "test").Success);
    }

    [Fact]
    public void Grant_SameTargetTwice_Replaces_DoesNotAccumulate()
    {
        var (manager, clock, _) = Create(At(21, 0));

        manager.Grant(BlockTarget.YouTube, 60, "test");   // -> 22:00
        clock.SetLocal(At(21, 30));
        manager.Grant(BlockTarget.YouTube, 10, "test");   // -> 21:40 (누적되면 22:10 이 되어버린다)

        var permit = manager.GetEffective(BlockTarget.YouTube);
        Assert.NotNull(permit);
        Assert.Equal(new DateTimeOffset(At(21, 40)), permit!.ExpireTimeUtc);
    }

    [Fact]
    public void AllPermit_CoversEveryTarget()
    {
        var (manager, clock, _) = Create(At(21, 0));

        manager.Grant(BlockTarget.All, 20, "test");

        clock.SetLocal(At(21, 10));
        Assert.NotNull(manager.GetEffective(BlockTarget.YouTube));
        Assert.NotNull(manager.GetEffective(BlockTarget.Roblox));

        clock.SetLocal(At(21, 20));
        Assert.Null(manager.GetEffective(BlockTarget.YouTube));
        Assert.Null(manager.GetEffective(BlockTarget.Roblox));
    }

    [Fact]
    public void GetEffective_PicksLatestExpiry()
    {
        var (manager, _, _) = Create(At(21, 0));

        manager.Grant(BlockTarget.YouTube, 10, "test"); // -> 21:10
        manager.Grant(BlockTarget.All, 60, "test");     // -> 22:00

        var permit = manager.GetEffective(BlockTarget.YouTube);
        Assert.NotNull(permit);
        Assert.Equal(new DateTimeOffset(At(22, 0)), permit!.ExpireTimeUtc);
    }

    [Fact]
    public void CancelAll_RemovesEverything()
    {
        var (manager, _, _) = Create(At(21, 0));

        manager.Grant(BlockTarget.YouTube, 30, "test");
        manager.Grant(BlockTarget.Roblox, 30, "test");

        var removed = manager.CancelAll("test");

        Assert.Equal(2, removed);
        Assert.Empty(manager.GetActive());
        Assert.Null(manager.GetEffective(BlockTarget.YouTube));
    }

    [Fact]
    public void SurvivesServiceRestart_ExpireTimeIsPreserved()
    {
        // 사양 예시: 21:00 에 YouTube 60분 허용 -> 21:20 재부팅 -> 22:00 에 정확히 차단
        var clock = new FixedClock(At(21, 0));
        var store = new InMemoryPermitStateStore();

        var first = new TemporaryPermitManager(store, clock, () => Settings);
        first.Grant(BlockTarget.YouTube, 60, "test");

        // 21:20 재부팅: 같은 store 를 읽는 새 매니저
        clock.SetLocal(At(21, 20));
        var second = new TemporaryPermitManager(store, clock, () => Settings);

        var restored = second.GetEffective(BlockTarget.YouTube);
        Assert.NotNull(restored);
        Assert.Equal(new DateTimeOffset(At(22, 0)), restored!.ExpireTimeUtc);

        clock.SetLocal(At(21, 59));
        Assert.NotNull(second.GetEffective(BlockTarget.YouTube));

        clock.SetLocal(At(22, 0));
        Assert.Null(second.GetEffective(BlockTarget.YouTube));
    }

    [Fact]
    public void RestartAfterExpiry_DoesNotRestorePermit()
    {
        var clock = new FixedClock(At(21, 0));
        var store = new InMemoryPermitStateStore();

        var first = new TemporaryPermitManager(store, clock, () => Settings);
        first.Grant(BlockTarget.YouTube, 30, "test");

        clock.SetLocal(At(23, 0)); // 만료 후 재시작
        var second = new TemporaryPermitManager(store, clock, () => Settings);

        Assert.Null(second.GetEffective(BlockTarget.YouTube));
        Assert.Empty(second.GetActive());
    }

    [Fact]
    public void PurgeExpired_RemovesOnlyExpired()
    {
        var (manager, clock, _) = Create(At(21, 0));

        manager.Grant(BlockTarget.YouTube, 10, "test"); // -> 21:10
        manager.Grant(BlockTarget.Roblox, 60, "test");  // -> 22:00

        clock.SetLocal(At(21, 30));
        var purged = manager.PurgeExpired();

        Assert.Equal(1, purged);
        Assert.Single(manager.GetActive());
        Assert.NotNull(manager.GetEffective(BlockTarget.Roblox));
    }

    [Fact]
    public void JsonStore_RoundTripsThroughDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "permits.json");

        try
        {
            var clock = new FixedClock(At(21, 0));
            var writeStore = new JsonPermitStateStore(path);
            var manager = new TemporaryPermitManager(writeStore, clock, () => Settings);
            manager.Grant(BlockTarget.Roblox, 45, "test");

            var readStore = new JsonPermitStateStore(path);
            var restored = readStore.Load();

            Assert.Single(restored);
            Assert.Equal(BlockTarget.Roblox, restored[0].Target);
            Assert.Equal(new DateTimeOffset(At(21, 45)), restored[0].ExpireTimeUtc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JsonStore_BrokenFile_ReturnsEmptyInsteadOfThrowing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TimeBlockerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "permits.json");

        try
        {
            File.WriteAllText(path, "{ this is not valid json");
            var store = new JsonPermitStateStore(path);
            Assert.Empty(store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
