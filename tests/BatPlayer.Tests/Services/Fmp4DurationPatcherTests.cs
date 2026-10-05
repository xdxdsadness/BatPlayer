using System;
using System.Linq;
using BatPlayer.Services.SoundCloud;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Тесты патча длительности склеенного fMP4: длительность в mvhd, вставка mehd,
/// коррекция размера moov. Фикстуры собираются вручную из боксов — структура
/// минимальна, но повторяет layout init-сегмента SoundCloud (mvhd v0, timescale 1000).
/// </summary>
public class Fmp4DurationPatcherTests
{
    [Fact]
    public void Patch_WritesDurationAndInsertsMehd()
    {
        var data = BuildFmp4(timescale: 1000, movieDuration: 0);
        const long durationMs = 142_439;

        var patched = Fmp4DurationPatcher.Patch(data, durationMs);

        // mehd вставлен (+16 байт), moov подрос, mvhd.duration = длительности в таймскейле.
        Assert.Equal(data.Length + 16, patched.Length);
        Assert.Equal(142_439, (int)ReadUInt32BE(patched, MvhdDurationOffset(data)));
        var (mehdOffset, mehdSize) = FindBox(patched, 0, patched.Length, "mehd");
        Assert.True(mehdOffset > 0);
        Assert.Equal(16, mehdSize);
        Assert.Equal(142_439, (int)ReadUInt32BE(patched, mehdOffset + 12));

        var (moovOffset, moovSize) = FindBox(patched, 0, patched.Length, "moov");
        // moov = заголовок(8) + mvhd(108) + mehd(16); поле размера боксa согласовано.
        Assert.Equal(8 + 108 + 16, moovSize);
        Assert.Equal(moovSize, (int)ReadUInt32BE(patched, moovOffset));

        // Исходные байты не тронуты (патч работает с копией).
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

        // mehd строго внутри moov и сразу после mvhd — читатели фрагментированного
        // mp4 ищут его именно там.
        Assert.Equal(mvhdOffset + mvhdSize, mehdOffset);
    }

    [Fact]
    public void Patch_TimescaleScalesDuration()
    {
        // timescale 44100: длительность пересчитывается из миллисекунд в юниты таймскейла.
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
        // Мусор/усечённый файл не должен падать: возвращаем как есть, файл остаётся
        // валидным для декодирования (пусть и без длительности).
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        Assert.Same(garbage, Fmp4DurationPatcher.Patch(garbage, 1000));

        var shortInput = new byte[] { 1, 2, 3 };
        Assert.Same(shortInput, Fmp4DurationPatcher.Patch(shortInput, 1000));

        var noMoov = BuildBox("free", new byte[16]);
        Assert.Same(noMoov, Fmp4DurationPatcher.Patch(noMoov, 1000));
    }

    /// <summary>Минимальный fMP4: ftyp + moov{mvhd(v0, timescale, duration)} + пара moof/mdat.</summary>
    private static byte[] BuildFmp4(uint timescale, uint movieDuration)
    {
        var mvhd = new byte[108]; // стандартный размер mvhd v0
        WriteUInt32BE(mvhd, 0, (uint)mvhd.Length);
        mvhd[4] = (byte)'m'; mvhd[5] = (byte)'v'; mvhd[6] = (byte)'h'; mvhd[7] = (byte)'d';
        // version=0/flags=0, creation/modification = 0; затем timescale и duration.
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

    /// <summary>Смещение поля duration в mvhd (v0: 12 заголовок + 8 дат + 4 timescale).</summary>
    private static int MvhdDurationOffset(byte[] data)
    {
        var (moovOffset, moovSize) = FindBox(data, 0, data.Length, "moov");
        var (mvhdOffset, _) = FindBox(data, moovOffset + 8, moovOffset + moovSize, "mvhd");
        return mvhdOffset + 24;
    }

    // Контейнерные боксы, внутрь которых спускаемся: mehd лежит внутри moov,
    // поэтому контейнер нельзя перепрыгивать целиком.
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
