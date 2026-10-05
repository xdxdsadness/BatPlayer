using System;

namespace BatPlayer.Helpers;

/// <summary>
/// Чистая логика «guard'а» перемотки: после seek'а мы оптимистично показываем
/// целевую позицию и глушим тики PositionChanged, пока движок не догонит цель —
/// иначе OneWay-биндинг мгновенно откатывает ползунок таймлайна на старую позицию.
/// Класс сознательно без таймеров и аудио: время передаётся снаружи
/// (в VM — Environment.TickCount64), поэтому логика покрывается юнит-тестами
/// без аудио-движка.
/// </summary>
public sealed class SeekSyncGuard
{
    /// <summary>Допуск «доехали»: позиция считается достигнутой, если p >= target - Epsilon.</summary>
    public static readonly TimeSpan Epsilon = TimeSpan.FromMilliseconds(500);

    /// <summary>Предохранитель: guard сам сбрасывается, если движок так и не догнал цель.</summary>
    public static readonly TimeSpan Fuse = TimeSpan.FromMilliseconds(1500);

    public bool IsActive { get; private set; }
    public TimeSpan Target { get; private set; }

    private long _startTicks;

    /// <summary>Включить guard (повторный вызов перевзводит на новую цель).</summary>
    public void Begin(TimeSpan target, long nowTicks)
    {
        Target = target;
        IsActive = true;
        _startTicks = nowTicks;
    }

    public void End() => IsActive = false;

    /// <summary>Истёк ли предохранитель (PositionChanged так и не дошёл до цели)?</summary>
    public bool IsExpired(long nowTicks)
        => IsActive && nowTicks - _startTicks >= (long)Fuse.TotalMilliseconds;

    /// <summary>
    /// Решение по входящему тику позиции: true — принять (и закрыть guard, если
    /// движок добрался до цели или истёк предохранитель), false — проглотить
    /// (тики ещё показывают старую позицию). При неактивном guard'е принимается всё.
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
