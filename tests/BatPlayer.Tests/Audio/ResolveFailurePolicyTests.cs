using BatPlayer.Audio;
using Xunit;

namespace BatPlayer.Tests.Audio;

/// <summary>
/// Policy for failed SC file resolves: a user click → StopWithError (play or clean error,
/// no skips); auto-transition → SkipNext capped at MaxConsecutiveUnresolvable in a row
/// (guards against Next looping on a dead queue).
/// </summary>
public class ResolveFailurePolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void Decide_UserInitiated_AlwaysStopsWithError(int streak)
    {
        // Explicit user intent: click → play or clean error, never a skip,
        // regardless of the failure streak.
        var action = ResolveFailurePolicy.Decide(userInitiated: true, streak);

        Assert.Equal(ResolveFailureAction.StopWithError, action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Decide_AutoTransition_BelowLimit_SkipsNext(int streak)
    {
        // Counter BEFORE the current failure: streak+1 < Max — dead track skipped fast.
        Assert.True(streak + 1 < ResolveFailurePolicy.MaxConsecutiveUnresolvable);
        var action = ResolveFailurePolicy.Decide(userInitiated: false, streak);

        Assert.Equal(ResolveFailureAction.SkipNext, action);
    }

    [Fact]
    public void Decide_AutoTransition_LimitReached_StopsWithError()
    {
        // Last allowed consecutive skip (streak+1 == Max) — stop with an error
        // instead of looping Next over a dead queue.
        var action = ResolveFailurePolicy.Decide(userInitiated: false, ResolveFailurePolicy.MaxConsecutiveUnresolvable - 1);

        Assert.Equal(ResolveFailureAction.StopWithError, action);
    }

    [Fact]
    public void Decide_AutoTransition_OverLimit_StopsWithError()
    {
        // Defensive case: streak not reset (should not happen — the counter resets
        // with Stop), but the policy must still Stop.
        var action = ResolveFailurePolicy.Decide(userInitiated: false, ResolveFailurePolicy.MaxConsecutiveUnresolvable);

        Assert.Equal(ResolveFailureAction.StopWithError, action);
    }
}
