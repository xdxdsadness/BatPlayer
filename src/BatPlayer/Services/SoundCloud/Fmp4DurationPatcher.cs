using System;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Writes the overall duration into an fMP4 stitched from HLS segments (init segment + moof/mdat).
/// SoundCloud's init segment has mvhd.duration = 0 and no mehd: Media Foundation opens and decodes
/// the file but reports TotalTime = 0, so the timeline, seeking and remaining time break. Patches
/// the mvhd duration and inserts a mehd into moov; the duration is written in the mvhd timescale
/// (1000 on SoundCloud init segments, i.e. milliseconds). On unreadable structure the original
/// bytes are returned unchanged — the file stays valid, just without a duration.
/// </summary>
public static class Fmp4DurationPatcher
{
    /// <summary>Max 32-bit duration field value in milliseconds (2^32 - 1 ms ≈ 49.7 days).</summary>
    private const long Max32BitMs = uint.MaxValue;

    /// <summary>Returns a copy of <paramref name="data"/> with duration = <paramref name="durationMs"/>.
    /// durationMs &lt;= 0 or unparsable structure — original bytes unchanged.</summary>
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
            // v0: version/flags(4) + creation(4) + modification(4); v1: same fields, 8 bytes each.
            int timescaleOffset = mvhdOffset + 12 + (version == 1 ? 16 : 8);
            int durationOffset = timescaleOffset + 4;
            int durationFieldSize = version == 1 ? 8 : 4;
            if (durationOffset + durationFieldSize > mvhdOffset + mvhdSize) return data;

            uint timescale = ReadUInt32BE(data, timescaleOffset);
            if (timescale == 0) return data;

            long duration = Math.Min(durationMs * timescale / 1000, version == 1 ? long.MaxValue : Max32BitMs);

            var patched = new byte[data.Length + 16];
            // mehd (movie extension header, v0), after mvhd inside moov: fragmented readers
            // take the full duration from it.
            var mehd = new byte[16];
            WriteUInt32BE(mehd, 0, 16);
            mehd[4] = (byte)'m'; mehd[5] = (byte)'e'; mehd[6] = (byte)'h'; mehd[7] = (byte)'d';
            WriteUInt32BE(mehd, 12, (uint)Math.Min(duration, Max32BitMs));

            int insertAt = mvhdOffset + mvhdSize;

            // copy everything up to the insertion point
            Buffer.BlockCopy(data, 0, patched, 0, insertAt);
            // mehd
            Buffer.BlockCopy(mehd, 0, patched, insertAt, mehd.Length);
            // tail
            Buffer.BlockCopy(data, insertAt, patched, insertAt + mehd.Length, data.Length - insertAt);

            // patch the mvhd duration (already in patched — same offsets)
            if (version == 1)
                WriteUInt64BE(patched, durationOffset, duration);
            else
                WriteUInt32BE(patched, durationOffset, (uint)duration);

            // moov grew by the mehd size
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
                // largesize (64-bit) — not patched, but skipped correctly.
                if (i + 16 > end) break;
                long large = (long)ReadUInt64BE(data, i + 8);
                if (large < 16) break;
                i += checked((int)Math.Min(large, int.MaxValue));
                continue;
            }
            if (size < 8) break; // zero/invalid size — stop walking

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
