using System;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Проставляет общую длительность в склеенном из HLS-сегментов fMP4 (init-сегмент + moof/mdat).
/// У init-сегмента SoundCloud mvhd.duration = 0 и mehd (movie extension header) отсутствует:
/// Media Foundation такой файл открывает и декодирует, но TotalTime = 0 — таймлайн, перемотка
/// и «оставшееся время» не работают. Патчим duration в mvhd и вставляем mehd внутрь moov;
/// длительность пишется в таймскейле mvhd (у SoundCloud init-сегментов — 1000, т.е. миллисекунды).
/// Структура не читается (битый/неожиданный layout) — возвращаем исходные байты: файл
/// остаётся валидным, пусть и без длительности.
/// </summary>
public static class Fmp4DurationPatcher
{
    /// <summary>Максимальное значение 32-битного поля duration в миллисекундах (2^32 - 1 мс ≈ 49.7 суток).</summary>
    private const long Max32BitMs = uint.MaxValue;

    /// <summary>Вернуть копию <paramref name="data"/> с duration = <paramref name="durationMs"/>.
    /// durationMs &lt;= 0 или структура не читается — исходные байты без изменений.</summary>
    public static byte[] Patch(byte[] data, long durationMs)
    {
        if (durationMs <= 0 || data.Length < 8)
            return data;

        try
        {
            var (moovOffset, moovSize) = FindBox(data, 0, data.Length, Moov);
            if (moovOffset < 0) return data;

            var (mvhdOffset, mvhdSize) = FindBox(data, moovOffset + 8, moovOffset + moovSize, Mvhd);
            if (mvhdOffset < 0) return data;

            var version = data[mvhdOffset + 8];
            // v0: version/flags(4) + creation(4) + modification(4); v1: те же поля по 8 байт.
            int timescaleOffset = mvhdOffset + 12 + (version == 1 ? 16 : 8);
            int durationOffset = timescaleOffset + 4;
            int durationFieldSize = version == 1 ? 8 : 4;
            if (durationOffset + durationFieldSize > mvhdOffset + mvhdSize) return data;

            uint timescale = ReadUInt32BE(data, timescaleOffset);
            if (timescale == 0) return data;

            long duration = Math.Min(durationMs * timescale / 1000, version == 1 ? long.MaxValue : Max32BitMs);

            var patched = new byte[data.Length + 16];
            // mehd (movie extension header, версия 0) — после mvhd внутри moov: именно по нему
            // fragmented-читатели берут полную длительность ролика.
            var mehd = new byte[16];
            WriteUInt32BE(mehd, 0, 16);
            mehd[4] = (byte)'m'; mehd[5] = (byte)'e'; mehd[6] = (byte)'h'; mehd[7] = (byte)'d';
            WriteUInt32BE(mehd, 12, (uint)Math.Min(duration, Max32BitMs));

            int insertAt = mvhdOffset + mvhdSize;

            // копируем всё до точки вставки
            Buffer.BlockCopy(data, 0, patched, 0, insertAt);
            // mehd
            Buffer.BlockCopy(mehd, 0, patched, insertAt, mehd.Length);
            // хвост
            Buffer.BlockCopy(data, insertAt, patched, insertAt + mehd.Length, data.Length - insertAt);

            // патчим duration в mvhd (уже в patched — смещения те же)
            if (version == 1)
                WriteUInt64BE(patched, durationOffset, duration);
            else
                WriteUInt32BE(patched, durationOffset, (uint)duration);

            // moov подрос на размер mehd
            WriteUInt32BE(patched, moovOffset, (uint)(moovSize + mehd.Length));

            return patched;
        }
        catch (Exception)
        {
            return data;
        }
    }

    private static readonly byte[] Moov = { (byte)'m', (byte)'o', (byte)'o', (byte)'v' };
    private static readonly byte[] Mvhd = { (byte)'m', (byte)'v', (byte)'h', (byte)'d' };

    private static (int offset, int size) FindBox(byte[] data, int start, int end, byte[] type)
    {
        int i = start;
        while (i + 8 <= end)
        {
            uint size = ReadUInt32BE(data, i);
            if (size == 1)
            {
                // largesize (64-бит) — не патчим, но идём дальше корректно.
                if (i + 16 > end) break;
                long large = (long)ReadUInt64BE(data, i + 8);
                if (large < 16) break;
                i += checked((int)Math.Min(large, int.MaxValue));
                continue;
            }
            if (size < 8) break; // нулевой/битый размер — дальше не идём

            if (data[i + 4] == type[0] && data[i + 5] == type[1] &&
                data[i + 6] == type[2] && data[i + 7] == type[3])
                return (i, checked((int)Math.Min(size, int.MaxValue)));

            i += checked((int)Math.Min(size, (uint)(end - i)));
        }
        return (-1, 0);
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
        => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static ulong ReadUInt64BE(byte[] data, int offset)
        => (ulong)ReadUInt32BE(data, offset) << 32 | ReadUInt32BE(data, offset + 4);

    private static void WriteUInt32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteUInt64BE(byte[] data, int offset, long value)
    {
        WriteUInt32BE(data, offset, (uint)(value >> 32));
        WriteUInt32BE(data, offset + 4, (uint)value);
    }
}
