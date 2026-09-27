using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// <see cref="FFOutputParser"/> — парсер вывода
/// <c>ffprobe -show_entries stream=channels,sample_rate</c>.
/// <para>
/// Регрессия: отсутствующий параметр возвращался как <c>0</c> — «0 каналов» было
/// неотличимо от «каналы неизвестны». Теперь «нема» — это <c>null</c>.
/// </para>
/// </summary>
public sealed class FFOutputParserTests
{
    [Fact]
    public void BothKeysPresent_ParsesBoth()
    {
        var (channels, sampleRate) = FFOutputParser.ParseChannelsAndSampleRate(
            "sample_rate=48000\nchannels=2\n");

        Assert.Equal(48000, sampleRate);
        Assert.Equal(2, channels);
    }

    [Fact]
    public void MissingKey_IsNullNotZero()
    {
        var (channels, sampleRate) = FFOutputParser.ParseChannelsAndSampleRate(
            "sample_rate=16000\n");

        Assert.Equal(16000, sampleRate);
        Assert.Null(channels);
    }

    [Fact]
    public void OnlyChannels_ParsesChannels()
    {
        var (channels, sampleRate) = FFOutputParser.ParseChannelsAndSampleRate(
            "channels=1\n");

        Assert.Equal(1, channels);
        Assert.Null(sampleRate);
    }

    [Fact]
    public void EmptyInput_IsAllNull()
    {
        var (channels, sampleRate) = FFOutputParser.ParseChannelsAndSampleRate("");

        Assert.Null(channels);
        Assert.Null(sampleRate);
    }

    [Fact]
    public void GarbageLinesAndEmptyValues_AreSkipped()
    {
        var (channels, sampleRate) = FFOutputParser.ParseChannelsAndSampleRate(
            "some warning line\n# comment\nsample_rate=\nchannels=2\n");

        Assert.Equal(2, channels);
        Assert.Null(sampleRate);
    }
}
