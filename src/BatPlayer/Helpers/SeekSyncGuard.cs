using System;

namespace BatPlayer.Helpers;

/// <summary>
/// Pure seek-guard logic: after a seek the target position is shown optimistically and
/// PositionChanged ticks are swallowed until the engine reaches the target — otherwise
/// the OneWay binding instantly snaps the timeline slider back to the old position.
/// The class deliberately has no timers or audio: time is passed in from outside
/// (Environment.TickCount64 in the VM), so the logic is unit-testable without an audio engine.
/// </summary>
public sealed class SeekSyncGuard
{
    /// <summary>"Arrived" tolerance: the position counts as reached when p >= target - Epsilon.</summary>
    public static readonly TimeSpan Epsilon = TimeSpan.FromMilliseconds(500);

    /// <summary>Fuse: the guard resets itself if the engine never reaches the target.</summary>
    public static readonly TimeSpan Fuse = TimeSpan.FromMilliseconds(1500);

    public bool IsActive { get; private set; }
    public TimeSpan Target { get; private set; }

    private long _startTicks;

    /// <summary>Start the guard (calling again re-arms it with a new target).</summary>
    public void Begin(TimeSpan target, long nowTicks)
    {
        Target = target;
        IsActive = true;
        _startTicks = nowTicks;
    }

    public void End() => IsActive = false;

    /// <summary>Has the fuse expired (PositionChanged never reached the target)?</summary>
    public bool IsExpired(long nowTicks)
        => IsActive && nowTicks - _startTicks >= (long)Fuse.TotalMilliseconds;

    /// <summary>
    /// Decision for an incoming position tick: true = accept (and close the guard if the
    /// engine reached the target or the fuse expired), false = swallow (ticks still show
    /// the old position). When the guard is inactive everything is accepted.
    /// </summary>
    public bool TryAccept(TimeSpan p, long nowTicks)
    {
        if (!IsActive) return true;
        if (IsExpired(nowTicks) || p >= Target - Epsilon)
        {
            End();
            return true;
        }
        return false;
    }
}
