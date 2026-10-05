using BatPlayer.Audio;
using Xunit;

namespace BatPlayer.Tests.Audio;

/// <summary>
/// Политика обработки неудачного резолва файла SC-трека: клик пользователя —
/// StopWithError (играет ИЛИ чистая ошибка, без перескоков), авто-переход —
/// SkipNext с лимитом MaxConsecutiveUnresolvable подряд (защита от зацикливания
/// Next на мёртвой очереди).
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
        // Явное намерение пользователя: кликнул → играет ИЛИ чистая ошибка,
        // никаких перескоков — независимо от счётчика подряд идущих неудач.
        var action = ResolveFailurePolicy.Decide(userInitiated: true, streak);

        Assert.Equal(ResolveFailureAction.StopWithError, action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Decide_AutoTransition_BelowLimit_SkipsNext(int streak)
    {
        // Счётчик ДО текущей неудачи: streak+1 < Max — мёртвый трек скипается быстро.
        Assert.True(streak + 1 < ResolveFailurePolicy.MaxConsecutiveUnresolvable);
        var action = ResolveFailurePolicy.Decide(userInitiated: false, streak);

        Assert.Equal(ResolveFailureAction.SkipNext, action);
    }

    [Fact]
    public void Decide_AutoTransition_LimitReached_StopsWithError()
    {
        // Последний разрешённый подряд пропуск (streak+1 == Max) — останавливаемся
        // с ошибкой вместо бесконечного цикла Next по мёртвой очереди.
        var action = ResolveFailurePolicy.Decide(userInitiated: false, ResolveFailurePolicy.MaxConsecutiveUnresolvable - 1);

        Assert.Equal(ResolveFailureAction.StopWithError, action);
    }

    [Fact]
    public void Decide_AutoTransition_OverLimit_StopsWithError()
    {
        // Защитный случай: streak уже не сброшен (не должен встречаться — счётчик
        // сбрасывается вместе с Stop), но политика обязана держать Stop.
        var action = ResolveFailurePolicy.Decide(userInitiated: false, ResolveFailurePolicy.MaxConsecutiveUnresolvable);

        Assert.Equal(ResolveFailureAction.StopWithError, action);
    }
}
