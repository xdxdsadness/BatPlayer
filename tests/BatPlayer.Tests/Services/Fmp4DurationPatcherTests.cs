using System;
using System.Linq;
using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Tests for fMP4 concat duration patching: mvhd duration, mehd insertion, moov size
/// correction. Fixtures are hand-built boxes — minimal but matching the SoundCloud
/// init-segment layout (mvhd v0, timescale 1000).
/// </summary>
public class Fmp4DurationPatcherTests
{
    [Fact]
    public void Patch_WritesDurationAndInsertsMehd()
    {
        var data = BuildFmp4(timescale: 1000, movieDuration: 0);
        const long durationMs = 142_439;

        var patched = Fmp4DurationPatcher.Patch(data, durationMs);

        // mehd inserted (+16 bytes), moov grew, mvhd.duration set in timescale units.
        Assert.Equal(data.Length + 16, patched.Length);
        Assert.Equal(142_439, (int)ReadUInt32BE(patched, MvhdDurationOffset(data)));
        var (mehdOffset, mehdSize) = FindBox(patched, 0, patched.Length, "mehd");
        Assert.True(mehdOffset > 0);
        Assert.Equal(16, mehdSize);
        Assert.Equal(142_439, (int)ReadUInt32BE(patched, mehdOffset + 12));

        var (moovOffset, moovSize) = FindBox(patched, 0, patched.Length, "moov");
        // moov = header(8) + mvhd(108) + mehd(16); size field is consistent.
        Assert.Equal(8 + 108 + 16, moovSize);
        Assert.Equal(moovSize, (int)ReadUInt32BE(patched, moovOffset));

        // Original bytes untouched (patch works on a copy).
        Assert.Equal(0, (int)ReadUInt32BE(data, MvhdDurationOffset(data)));
    }

    [Fact]
    public void Patch_MehdPlacedInsideMoov_AfterMvhd()
    {
        var data = BuildFmp4(timescale: 44_100, movieDuration: 0);
        var patched = Fmp4DurationPatcher.Patch(data, 1000);

        var (moovOffset, moovSize) = FindBox(patched, 0, patched.Length, "moov");
        var (mvhdOffset, mvhdSize) = FindBox(patched, moovOffset, moovOffset + moovSize, "mvhd");
        var (mehdOffset, _) = FindBox(patched, moovOffset, moovOffset + moovSize, "mehd");

        // mehd sits strictly inside moov right after mvhd — exactly where fragmented
        // mp4 readers look for it.
        Assert.Equal(mvhdOffset + mvhdSize, mehdOffset);
    }

    [Fact]
    public void Patch_TimescaleScalesDuration()
    {
        // timescale 44100: ms duration converted to timescale units.
        var data = BuildFmp4(timescale: 44_100, movieDuration: 0);

        var patched = Fmp4DurationPatcher.Patch(data, 1000);

        Assert.Equal(44_100, (int)ReadUInt32BE(patched, MvhdDurationOffset(data)));
    }

    [Fact]
    public void Patch_ZeroOrNegativeDuration_ReturnsOriginalBytes()
    {
        var data = BuildFmp4(timescale: 1000, movieDuration: 0);

        Assert.Same(data, Fmp4DurationPatcher.Patch(data, 0));
        Assert.Same(data, Fmp4DurationPatcher.Patch(data, -5));
    }

    [Fact]
    public void Patch_GarbageInput_ReturnsOriginalBytes()
    {
        // Garbage/truncated input must not throw: return as-is, the file stays
        // decodable (just without duration).
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        Assert.Same(garbage, Fmp4DurationPatcher.Patch(garbage, 1000));

        var shortInput = new byte[] { 1, 2, 3 };
        Assert.Same(shortInput, Fmp4DurationPatcher.Patch(shortInput, 1000));

        var noMoov = BuildBox("free", new byte[16]);
        Assert.Same(noMoov, Fmp4DurationPatcher.Patch(noMoov, 1000));
    }

    /// <summary>Minimal fMP4: ftyp + moov{mvhd(v0, timescale, duration)} + a moof/mdat pair.</summary>
    private static byte[] BuildFmp4(uint timescale, uint movieDuration)
    {
        var mvhd = new byte[108]; // standard mvhd v0 size
        WriteUInt32BE(mvhd, 0, (uint)mvhd.Length);
        mvhd[4] = (byte)'m'; mvhd[5] = (byte)'v'; mvhd[6] = (byte)'h'; mvhd[7] = (byte)'d';
        // version=0/flags=0, creation/modification = 0; then timescale and duration.
        WriteUInt32BE(mvhd, 20, timescale);
        WriteUInt32BE(mvhd, 24, movieDuration);

        var moov = BuildBox("moov", mvhd);
        var ftyp = BuildBox("ftyp", new byte[16]);
        var moof = BuildBox("moof", new byte[32]);
        var mdat = BuildBox("mdat", new byte[64]);

        return ftyp.Concat(moov).Concat(moof).Concat(mdat).Concat(mdat).ToArray();
    }

    private static byte[] BuildBox(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        WriteUInt32BE(box, 0, (uint)box.Length);
        box[4] = (byte)type[0]; box[5] = (byte)type[1]; box[6] = (byte)type[2]; box[7] = (byte)type[3];
        payload.CopyTo(box, 8);
        return box;
    }

    /// <summary>Offset of the duration field in mvhd (v0: 12 header + 8 dates + 4 timescale).</summary>
    private static int MvhdDurationOffset(byte[] data)
    {
        var (moovOffset, moovSize) = FindBox(data, 0, data.Length, "moov");
        var (mvhdOffset, _) = FindBox(data, moovOffset + 8, moovOffset + moovSize, "mvhd");
        return mvhdOffset + 24;
    }

    // Container boxes we descend into: mehd lives inside moov, so containers must
    // not be skipped wholesale.
    private static readonly System.Collections.Generic.HashSet<string> ContainerBoxes =
        new() { "moov", "trak", "mdia", "minf", "stbl", "mvex", "edts", "udta" };

    private static (int offset, int size) FindBox(byte[] data, int start, int end, string type)
    {
        int i = start;
        while (i + 8 <= end)
        {
            uint size = ReadUInt32BE(data, i);
            if (size < 8) break;
            if (data[i + 4] == (byte)type[0] && data[i + 5] == (byte)type[1] &&
                data[i + 6] == (byte)type[2] && data[i + 7] == (byte)type[3])
                return (i, (int)size);
            var boxType = System.Text.Encoding.ASCII.GetString(data, i + 4, 4);
            i += ContainerBoxes.Contains(boxType) ? 8 : (int)Math.Min(size, (uint)(end - i));
        }
        return (-1, 0);
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
        => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static void WriteUInt32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
