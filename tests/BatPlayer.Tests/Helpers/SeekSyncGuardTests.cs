using System;
using BatPlayer.Helpers;
using Xunit;

namespace BatPlayer.Tests.Helpers;

/// <summary>
/// Seek-guard logic (SeekSyncGuard) — pure, no audio engine. PlayerBarViewModel itself
/// cannot be unit-tested (its constructor needs AudioService with an NAudio WASAPI engine
/// and the class is sealed), so the extracted guard logic is tested directly.
/// </summary>
public class SeekSyncGuardTests
{
    private const long T0 = 100_000; // arbitrary "current" mark, ms

    [Fact]
    public void InactiveGuard_acceptsAnyPosition()
    {
        var g = new SeekSyncGuard();

        Assert.False(g.IsActive);
        Assert.True(g.TryAccept(TimeSpan.FromSeconds(3), T0));
        Assert.True(g.TryAccept(TimeSpan.Zero, T0));
        Assert.False(g.IsActive);
    }

    [Fact]
    public void ActiveGuard_rejectsStalePositions_andStaysActive()
    {
        var g = new SeekSyncGuard();
        g.Begin(TimeSpan.FromSeconds(100), T0);

        Assert.True(g.IsActive);
        Assert.False(g.TryAccept(TimeSpan.FromSeconds(30), T0 + 100));
        Assert.False(g.TryAccept(TimeSpan.FromSeconds(99.4), T0 + 200));
        Assert.True(g.IsActive); // not there yet — guard holds
    }

    [Fact]
    public void Guard_opens_whenEngineReachesTarget()
    {
        var g = new SeekSyncGuard();
        g.Begin(TimeSpan.FromSeconds(100), T0);

        Assert.False(g.TryAccept(TimeSpan.FromSeconds(80), T0 + 100));
        Assert.True(g.TryAccept(TimeSpan.FromSeconds(100), T0 + 200));
        Assert.False(g.IsActive); // caught up — guard closed
        // later ticks accepted as usual
        Assert.True(g.TryAccept(TimeSpan.FromSeconds(101), T0 + 300));
    }

    [Fact]
    public void EpsilonBoundary_halfSecondBeforeTarget_countsAsReached()
    {
        var target = TimeSpan.FromSeconds(100);
        var g = new SeekSyncGuard();
        g.Begin(target, T0);

        // exactly target - 0.5s counts as reached (p >= target - epsilon)
        Assert.True(g.TryAccept(target - SeekSyncGuard.Epsilon, T0 + 100));
        Assert.False(g.IsActive);

        // slightly earlier — not yet
        var g2 = new SeekSyncGuard();
        g2.Begin(target, T0);
        Assert.False(g2.TryAccept(target - SeekSyncGuard.Epsilon - TimeSpan.FromMilliseconds(1), T0 + 100));
        Assert.True(g2.IsActive);
    }

    [Fact]
    public void Fuse_releasesGuard_evenIfPositionNeverArrives()
    {
        var g = new SeekSyncGuard();
        g.Begin(TimeSpan.FromSeconds(100), T0);

        // 1ms before fuse expiry — still swallowed
        var fuseMs = (long)SeekSyncGuard.Fuse.TotalMilliseconds;
        Assert.False(g.TryAccept(TimeSpan.FromSeconds(10), T0 + fuseMs - 1));
        Assert.True(g.IsActive);

        // after expiry the fuse releases the guard; tick accepted
        Assert.True(g.IsExpired(T0 + fuseMs));
        Assert.True(g.TryAccept(TimeSpan.FromSeconds(10), T0 + fuseMs));
        Assert.False(g.IsActive);
    }

    [Fact]
    public void ReBegin_rearmsWithNewTarget()
    {
        var g = new SeekSyncGuard();
        g.Begin(TimeSpan.FromSeconds(100), T0);
        g.Begin(TimeSpan.FromSeconds(10), T0 + 500);

        Assert.Equal(TimeSpan.FromSeconds(10), g.Target);
        // old position counts as fresh against the NEW target
        Assert.True(g.TryAccept(TimeSpan.FromSeconds(9.8), T0 + 600));
        Assert.False(g.IsActive);
    }

    [Fact]
    public void End_releasesGuardImmediately()
    {
        var g = new SeekSyncGuard();
        g.Begin(TimeSpan.FromSeconds(100), T0);

        g.End();

        Assert.False(g.IsActive);
        Assert.True(g.TryAccept(TimeSpan.FromSeconds(1), T0 + 10));
    }
}
