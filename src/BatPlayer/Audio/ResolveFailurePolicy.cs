namespace BatPlayer.Audio;

/// <summary>Что делать при неудачном резолве файла SC-трека.</summary>
public enum ResolveFailureAction
{
    /// <summary>Перейти к следующему треку очереди (быстрый скип мёртвых при авто-переходе).</summary>
    SkipNext,

    /// <summary>Остановить воспроизведение и показать ошибку (без перескока).</summary>
    StopWithError
}

/// <summary>
/// Чистая политика обработки неудачного резолва FilePathResolver (SC-runtime-карточки).
/// Клики пользователя (PlayTrack/Play) — явное намерение: играет ИЛИ чистая ошибка,
/// никаких перескоков. Авто-переходы (Next/конец трека) — мёртвые треки скипаем,
/// но не более MaxConsecutiveUnresolvable подряд, иначе Next() зациклится на мёртвой
/// очереди (RepeatAll). streak — число уже учтённых подряд идущих неудачных резолвов
/// ДО текущей (успешный резолв сбрасывает счётчик).
/// </summary>
public static class ResolveFailurePolicy
{
    /// <summary>
    /// Лимит подряд неуспешных резолвов при авто-переходе: достигнут — Stop + тост,
    /// иначе Next() ходит по кругу, мигая иконкой Play/Pause.
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
