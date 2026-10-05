using System;
using System.Collections.Generic;
using NAudio.Wave;
using BatPlayer.Audio;
using Xunit;

namespace BatPlayer.Tests.Audio;

/// <summary>Простой float-источник с заранее заданными сэмплами для проверки маппинга.</summary>
internal sealed class ScriptedSampleSource : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public ScriptedSampleSource(int channels, int sampleRate, float[] samples)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _samples = samples;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _samples.Length - _position);
        if (n > 0) Array.Copy(_samples, _position, buffer, offset, n);
        _position += n;
        return n;
    }
}

public class ChannelMappingSampleProviderTests
{
    [Theory]
    [InlineData(2, 6)]
    [InlineData(6, 2)]
    [InlineData(1, 4)]
    [InlineData(4, 1)]
    public void WaveFormat_ReportsTargetChannelsAndSourceRate(int sourceChannels, int targetChannels)
    {
        var src = new ScriptedSampleSource(sourceChannels, 44100, new float[sourceChannels]);
        var mapper = new ChannelMappingSampleProvider(src, targetChannels);

        Assert.Equal(targetChannels, mapper.WaveFormat.Channels);
        Assert.Equal(44100, mapper.WaveFormat.SampleRate);
    }

    [Fact]
    public void Downmix_FourToTwo_AveragesPairsRoundRobin()
    {
        // Раскладка по кругу: каналы 0,2 -> выход 0; каналы 1,3 -> выход 1.
        var src = new ScriptedSampleSource(4, 48000, new[] { 0.1f, 0.2f, 0.3f, 0.4f });
        var mapper = new ChannelMappingSampleProvider(src, 2);

        var buffer = new float[8];
        var read = mapper.Read(buffer, 0, buffer.Length);

        Assert.Equal(2, read); // один кадр = 2 сэмпла
        Assert.Equal((0.1f + 0.3f) / 2, buffer[0], 5);
        Assert.Equal((0.2f + 0.4f) / 2, buffer[1], 5);
    }

    [Fact]
    public void Upmix_TwoToFour_PassesThroughAndPadsSilence()
    {
        var src = new ScriptedSampleSource(2, 48000, new[] { 0.5f, -0.25f });
        var mapper = new ChannelMappingSampleProvider(src, 4);

        var buffer = new float[8];
        var read = mapper.Read(buffer, 0, buffer.Length);

        Assert.Equal(4, read);
        Assert.Equal(0.5f, buffer[0], 5);
        Assert.Equal(-0.25f, buffer[1], 5);
        Assert.Equal(0f, buffer[2], 5);
        Assert.Equal(0f, buffer[3], 5);
    }

    [Fact]
    public void Downmix_SixToTwo_KeepsFrontLeftRightPositions()
    {
        // 5.1 -> 2.0: L усредняет (FL, FC, BL), R усредняет (FR, LFE, BR).
        var src = new ScriptedSampleSource(6, 44100, new[] { 0.6f, 0.5f, 0.4f, 0.3f, 0.2f, 0.1f });
        var mapper = new ChannelMappingSampleProvider(src, 2);

        var buffer = new float[16];
        var read = mapper.Read(buffer, 0, buffer.Length);

        Assert.Equal(2, read);
        Assert.Equal((0.6f + 0.4f + 0.2f) / 3, buffer[0], 5);
        Assert.Equal((0.5f + 0.3f + 0.1f) / 3, buffer[1], 5);
    }

    [Fact]
    public void Read_RespectsOffsetAndDoesNotExceedRequestedCount()
    {
        var samples = new List<float>();
        for (int f = 0; f < 100; f++)
            samples.Add(f % 2 == 0 ? 0.25f : -0.75f);

        var src = new ScriptedSampleSource(2, 48000, samples.ToArray());
        var mapper = new ChannelMappingSampleProvider(src, 2);

        var buffer = new float[64];
        var read = mapper.Read(buffer, 10, 40);

        // Запрошено 40 сэмплов при offset 10: вернулось <= 40, запись не за границами.
        Assert.True(read <= 40);
        Assert.Equal(0f, buffer[0], 5);   // до offset — не тронут
        Assert.Equal(0f, buffer[50], 5);  // после offset+40 — не тронут
        Assert.Equal(0.25f, buffer[10], 5);
    }

    [Fact]
    public void Read_ZeroFramesAvailable_ReturnsZero()
    {
        var src = new ScriptedSampleSource(2, 48000, Array.Empty<float>());
        var mapper = new ChannelMappingSampleProvider(src, 2);

        var buffer = new float[16];
        Assert.Equal(0, mapper.Read(buffer, 0, buffer.Length));
    }
}
