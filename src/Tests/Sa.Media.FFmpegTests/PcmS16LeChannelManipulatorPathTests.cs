using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;
using Sa.Media.FFmpegTests.Fakes;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// Разрешение выходных путей манипулятора относительно <see cref="FFMpegOptions.WritableDirectory"/>.
/// <para>
/// Регрессия: моно-ветка <c>SplitAsync</c> идёт через <see cref="FFMpegExecutor"/> (учитывает
/// <c>WritableDirectory</c>), а стерео-ветка и <c>JoinAsync</c> — через raw-executor (не учитывал).
/// При относительном имени файлы попадали в разные каталоги, а возвращённые пути могли вообще
/// не существовать. Оба пути обязаны разрешать имя одинаково — и возвращать разрешённый путь.
/// </para>
/// </summary>
public sealed class PcmS16LeChannelManipulatorPathTests : IDisposable
{
    static readonly string Input = Path.GetFullPath("./data/input.wav");

    readonly TempDirectory _writable = new();

    public void Dispose() => _writable.Dispose();

    [Fact]
    public async Task SplitAsync_Stereo_BareOutputName_IsResolvedAgainstWritableDirectory()
    {
        (IPcmS16LeChannelManipulator manipulator, RecordingProcessExecutor process) = Create(channels: 2);

        IReadOnlyList<string> files = await manipulator.SplitAsync(
            Input, "bare.wav", cancellationToken: TestContext.Current.CancellationToken);

        string[] expected =
        [
            _writable.Combine("bare_channel_0.wav"),
            _writable.Combine("bare_channel_1.wav"),
        ];

        Assert.Equal(expected, files);

        // Команда ffmpeg должна называть те же файлы, что и вернулись вызывающему.
        Assert.Contains(expected[0], process.LastCommand, StringComparison.Ordinal);
        Assert.Contains(expected[1], process.LastCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitAsync_Mono_BareOutputName_IsResolvedAgainstWritableDirectory()
    {
        (IPcmS16LeChannelManipulator manipulator, _) = Create(channels: 1);

        IReadOnlyList<string> files = await manipulator.SplitAsync(
            Input, "mono.wav", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([_writable.Combine("mono_channel_0.wav")], files);
    }

    [Fact]
    public async Task JoinAsync_BareOutputName_IsResolvedAgainstWritableDirectory()
    {
        (IPcmS16LeChannelManipulator manipulator, RecordingProcessExecutor process) = Create(channels: 2);

        string result = await manipulator.JoinAsync(
            Input, Input, "joined.wav", cancellationToken: TestContext.Current.CancellationToken);

        string expected = _writable.Combine("joined.wav");

        Assert.Equal(expected, result);
        Assert.Contains(expected, process.LastCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitAsync_ExplicitDirectory_IsKeptEvenWhenWritableDirectoryIsSet()
    {
        (IPcmS16LeChannelManipulator manipulator, _) = Create(channels: 2);

        string requested = _writable.Combine(Path.Combine("explicit", "out.wav"));

        IReadOnlyList<string> files = await manipulator.SplitAsync(
            Input, requested, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [_writable.Combine(Path.Combine("explicit", "out_channel_0.wav")),
             _writable.Combine(Path.Combine("explicit", "out_channel_1.wav"))],
            files);
    }

    (IPcmS16LeChannelManipulator Manipulator, RecordingProcessExecutor Process) Create(int channels)
    {
        var process = new RecordingProcessExecutor
        {
            // Ответ ffprobe на GetChannelsAndSampleRateAsync.
            StandardOutput = $"channels={channels}\nsample_rate=44100\n",
        };

        var mpeg = new FFMpegExecutor(
            new FFRawExecutor(process, "/ffmpeg", TimeSpan.FromSeconds(30)),
            _writable.Path);
        var probe = new FFProbeExecutor(
            new FFRawExecutor(process, "/ffprobe", TimeSpan.FromSeconds(30)));

        return (new PcmS16LeChannelManipulator(mpeg, probe, _writable.Path), process);
    }
}
