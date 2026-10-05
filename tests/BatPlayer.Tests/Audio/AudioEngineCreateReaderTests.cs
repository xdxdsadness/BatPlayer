using System;
using System.IO;
using NAudio.Vorbis;
using BatPlayer.Audio;
using Xunit;

namespace BatPlayer.Tests.Audio;

/// <summary>
/// Проверки выбора reader'а по расширению (AudioEngine.CreateReader, internal для тестов).
/// </summary>
public class AudioEngineCreateReaderTests
{
    private static string TestDataPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void CreateReader_OggFile_ReturnsVorbisWaveReader()
    {
        var path = TestDataPath("1test.ogg");
        Assert.True(File.Exists(path), "Тестовая фикстура TestData/1test.ogg не скопирована в выходную папку");

        using var reader = AudioEngine.CreateReader(path);

        Assert.NotNull(reader);
        Assert.IsType<VorbisWaveReader>(reader);
        Assert.True(reader!.TotalTime > TimeSpan.Zero, "Ogg Vorbis reader должен сообщать длительность");
    }

    [Fact]
    public void CreateReader_OgaExtension_HandledByVorbisDecoder()
    {
        // .oga — тот же контейнер Ogg; фикстуру копируем под новым расширением.
        var path = Path.Combine(Path.GetTempPath(), "obsidian_test_" + Guid.NewGuid().ToString("N") + ".oga");
        File.Copy(TestDataPath("1test.ogg"), path);
        try
        {
            using var reader = AudioEngine.CreateReader(path);
            Assert.NotNull(reader);
            Assert.IsType<VorbisWaveReader>(reader);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateReader_MissingFile_ReturnsNullWithoutThrowing()
    {
        Assert.Null(AudioEngine.CreateReader(TestDataPath("no_such_file.mp3")));
    }

    [Fact]
    public void CreateReader_CorruptOgg_FallsBackToMediaFoundationAndReturnsNull()
    {
        // Мусорные байты в .ogg: VorbisWaveReader падает, Media Foundation тоже —
        // CreateReader возвращает null и не бросает исключение.
        var path = Path.Combine(Path.GetTempPath(), "obsidian_test_garbage_" + Guid.NewGuid().ToString("N") + ".ogg");
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        try
        {
            Assert.Null(AudioEngine.CreateReader(path));
        }
        finally
        {
            // VorbisWaveReader при падении конструктора не закрывает открытый FileStream
            // (утечка внутри NAudio.Vorbis 1.5.0) — хендл держит файл до финализатора.
            // Без принудительного GC File.Delete иногда ловит «file is being used».
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { File.Delete(path); } catch (IOException) { } // хвостовой финализатор мог не успеть
        }
    }

    [Fact]
    public void CreateReader_UnsupportedExtension_GarbageContent_ReturnsNull()
    {
        // Неизвестное расширение уходит в Media Foundation; на мусорных байтах -> null.
        var path = Path.Combine(Path.GetTempPath(), "obsidian_test_garbage_" + Guid.NewGuid().ToString("N") + ".xyz");
        File.WriteAllBytes(path, new byte[] { 0x0A, 0x0B, 0x0C });
        try
        {
            Assert.Null(AudioEngine.CreateReader(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
