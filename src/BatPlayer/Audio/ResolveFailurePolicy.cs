namespace BatPlayer.Audio;

/// <summary>What to do when an SC track's file resolve fails.</summary>
public enum ResolveFailureAction
{
    /// <summary>Move to the next queue track (fast skip of dead tracks on auto-advance).</summary>
    SkipNext,

    /// <summary>Stop playback and show an error (no skipping).</summary>
    StopWithError
}

/// <summary>
/// Pure policy for a failed FilePathResolver resolve (SC runtime cards).
/// User clicks (PlayTrack/Play) are explicit intent: the track plays OR a clean
/// error shows, never a skip. Auto-advance (Next/track end) skips dead tracks,
/// but no more than MaxConsecutiveUnresolvable in a row, otherwise Next() loops
/// on a dead queue (RepeatAll). streak — the number of already-counted consecutive
/// failed resolves BEFORE the current one (a successful resolve resets the counter).
/// </summary>
public static class ResolveFailurePolicy
{
    /// <summary>
    /// Limit of consecutive failed resolves on auto-advance: once reached — Stop + toast,
    /// otherwise Next() goes in circles flashing the Play/Pause icon.
    /// </summary>
    public const int MaxConsecutiveUnresolvable = 3;

    public static ResolveFailureAction Decide(bool userInitiated, int streak)
        => (userInitiated, streak: streak + 1 >= MaxConsecutiveUnresolvable) switch
        {
            (true, _)     => ResolveFailureAction.StopWithError,
            (false, true) => ResolveFailureAction.StopWithError,
            _             => ResolveFailureAction.SkipNext
        };
}
