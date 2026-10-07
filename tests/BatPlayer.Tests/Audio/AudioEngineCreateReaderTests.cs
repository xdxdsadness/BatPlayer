using System;
using System.IO;
using NAudio.Vorbis;
using BatPlayer.Audio;
using Xunit;

namespace BatPlayer.Tests.Audio;

/// <summary>
/// Reader selection by extension (AudioEngine.CreateReader, internal for tests).
/// </summary>
public class AudioEngineCreateReaderTests
{
    private static string TestDataPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void CreateReader_OggFile_ReturnsVorbisWaveReader()
    {
        var path = TestDataPath("1test.ogg");
        Assert.True(File.Exists(path), "Test fixture TestData/1test.ogg was not copied to the output folder");

        using var reader = AudioEngine.CreateReader(path);

        Assert.NotNull(reader);
        Assert.IsType<VorbisWaveReader>(reader);
        Assert.True(reader!.TotalTime > TimeSpan.Zero, "Ogg Vorbis reader must report a duration");
    }

    [Fact]
    public void CreateReader_OgaExtension_HandledByVorbisDecoder()
    {
        // .oga is the same Ogg container; the fixture is copied under the new extension.
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
        // Garbage bytes in .ogg: VorbisWaveReader throws, Media Foundation too —
        // CreateReader returns null without throwing.
        var path = Path.Combine(Path.GetTempPath(), "obsidian_test_garbage_" + Guid.NewGuid().ToString("N") + ".ogg");
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        try
        {
            Assert.Null(AudioEngine.CreateReader(path));
        }
        finally
        {
            // VorbisWaveReader leaks an open FileStream when its constructor throws
            // (bug in NAudio.Vorbis 1.5.0) — the handle holds the file until finalization.
            // Without forced GC, File.Delete sometimes hits "file is being used".
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { File.Delete(path); } catch (IOException) { } // trailing finalizer may not have run
        }
    }

    [Fact]
    public void CreateReader_UnsupportedExtension_GarbageContent_ReturnsNull()
    {
        // Unknown extension goes to Media Foundation; garbage bytes → null.
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
