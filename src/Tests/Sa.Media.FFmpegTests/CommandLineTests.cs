using Sa.Media.FFmpeg.Services;
using Sa.Media.FFmpegTests.Fakes;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// Проверки того, какие именно флаги уходят в FFmpeg.
/// <para>
/// Формулировка «просто не упало» здесь недостаточна: почти любой набор флагов даёт успешную
/// конвертацию. Ошибки этого класса молча меняют результат — FFmpeg перезапишет файл поверх
/// существующего, заблокируется на stdin, возьмёт не ту аудиодорожку. Поэтому состав команды
/// проверяется явно, на подменённом исполнителе, без запуска процесса.
/// </para>
/// </summary>
public sealed class CommandLineTests
{
    static readonly string Input = Path.GetFullPath("./data/input.wav");
    static readonly string Output = Path.GetFullPath("./data/out.wav");

    public CommandLineTests()
    {
        // RecordingProcessExecutor создаёт выходные файлы успешных файловых команд, а
        // каталог с результатами не чистится между запусками. Удаляем их перед каждым
        // методом: тесты обязаны быть идемпотентными независимо от порядка и от
        // артефактов предыдущих запусков (иначе «isOverwrite=false + файл есть» валит
        // тест, который в чистом каталоге прошёл бы).
        foreach (var file in new[] { Output, Path.Combine(Path.GetDirectoryName(Output)!, "out put.wav") })
            if (File.Exists(file))
                File.Delete(file);
    }

    static (FFMpegExecutor Executor, RecordingProcessExecutor Process) CreateMpegExecutor()
    {
        var process = new RecordingProcessExecutor();
        var raw = new FFRawExecutor(process, "/ffmpeg", TimeSpan.FromSeconds(30));
        return (new FFMpegExecutor(raw), process);
    }

    static FFProbeExecutor CreateProbeExecutor(RecordingProcessExecutor process)
    {
        var raw = new FFRawExecutor(process, "/ffprobe", TimeSpan.FromSeconds(30));
        return new FFProbeExecutor(raw);
    }

    static string[] Args(RecordingProcessExecutor process) => FFmpegArgsTests.SplitArguments(process.LastCommand).ToArray();

    [Fact]
    public async Task ConvertToMp3_AlwaysPassesNostdin()
    {
        // -nostdin: без него FFmpeg при перенаправленном stdin задаёт «Overwrite? [y/N]»
        // и ждёт ответа, которого не будет. Проявилось бы зависанием до таймаута.
        var (mpeg, process) = CreateMpegExecutor();

        await mpeg.ConvertToMp3(Input, Output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("-nostdin", Args(process));
    }

    [Fact]
    public async Task ConvertToMp3_WhenOverwriteAllowed_PassesDashY()
    {
        var (mpeg, process) = CreateMpegExecutor();

        await mpeg.ConvertToMp3(Input, Output, isOverwrite: true, cancellationToken: TestContext.Current.CancellationToken);

        var args = Args(process);
        Assert.Contains("-y", args);
        Assert.DoesNotContain("-n", args);
    }

    [Fact]
    public async Task ConvertToMp3_WhenOverwriteForbidden_PassesDashN()
    {
        // Регрессия: раньше при isOverwrite: false не передавалось ни -y, ни -n, и FFmpeg
        // печатал вопрос в stdin и блокировался.
        var (mpeg, process) = CreateMpegExecutor();

        await mpeg.ConvertToMp3(Input, Output, isOverwrite: false, cancellationToken: TestContext.Current.CancellationToken);

        var args = Args(process);
        Assert.Contains("-n", args);
        Assert.DoesNotContain("-y", args);
    }

    [Fact]
    public async Task ConvertToPcmS16Le_TakesTheFirstAudioStreamExplicitly()
    {
        // Без -map ffmpeg сам выбирает «лучший» поток, и в многоязычной дорожке это может
        // оказаться не та дорожка — в зависимости от кодека исходника.
        var (mpeg, process) = CreateMpegExecutor();

        await mpeg.ConvertToPcmS16Le(Input, Output, cancellationToken: TestContext.Current.CancellationToken);

        var args = Args(process);
        Assert.Contains("-map", args);
        Assert.Contains("0:a:0", args);
    }

    [Fact]
    public async Task ConvertToPcmS16Le_QuotesPathsContainingSpaces()
    {
        var spaced = Path.Combine(Path.GetDirectoryName(Output)!, "out put.wav");
        var (mpeg, process) = CreateMpegExecutor();

        await mpeg.ConvertToPcmS16Le(Input, spaced, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(spaced, Args(process));
    }

    [Fact]
    public async Task ConvertToPcmS16Le_ResolvesBareFileNameAgainstWritableDirectory()
    {
        // Регрессия: WritableDirectory существовал в опциях, но не читался нигде.
        var writable = Path.GetTempPath();
        var process = new RecordingProcessExecutor();
        var raw = new FFRawExecutor(process, "/ffmpeg", TimeSpan.FromSeconds(30));
        var mpeg = new FFMpegExecutor(raw, writable);

        await mpeg.ConvertToPcmS16Le(Input, "bare.wav", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(writable, "bare.wav"), Args(process).Last());
    }

    [Fact]
    public async Task ConvertToPcmS16Le_KeepsExplicitDirectoryEvenWhenWritableDirectoryIsSet()
    {
        var process = new RecordingProcessExecutor();
        var raw = new FFRawExecutor(process, "/ffmpeg", TimeSpan.FromSeconds(30));
        var mpeg = new FFMpegExecutor(raw, Path.GetTempPath());

        await mpeg.ConvertToPcmS16Le(Input, Output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Output, Args(process).Last());
    }

    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("ogg")]
    [InlineData("s16le")]
    [InlineData("f32le")]
    public async Task ConvertToPcmS16LeFromStream_RejectsFormatNamesThatAreNotPlainIdentifiers(string format)
    {
        var (mpeg, process) = CreateMpegExecutor();
        using var input = new MemoryStream([1, 2, 3]);

        await mpeg.ConvertToPcmS16LeRaw(input, format, (_, _) => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);

        var args = Args(process);
        Assert.Contains("-f", args);
        Assert.Contains(format, args);
    }

    [Fact]
    public async Task ConvertToPcmS16LeFromStream_RejectsFormatNameWithInjectedArguments()
    {
        var (mpeg, _) = CreateMpegExecutor();
        using var input = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => mpeg.ConvertToPcmS16LeRaw(input, "wav -i /etc/passwd", (_, _) => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConvertToPcmS16LeFromStream_WritesToStdout()
    {
        var (mpeg, process) = CreateMpegExecutor();
        using var input = new MemoryStream([1, 2, 3]);

        await mpeg.ConvertToPcmS16LeRaw(input, "wav", (_, _) => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("pipe:1", Args(process).Last());
    }

    [Fact]
    public async Task FailsLoudly_WhenFFmpegExitsNonZero()
    {
        // Регрессия: конвертации шли с throwOnError: false, поэтому отсутствие файла на входе
        // возвращалось как пустая строка и вызывающий получал «успех».
        var (mpeg, process) = CreateMpegExecutor();
        process.ExitCode = 1;
        process.StandardError = "No such file or directory";

        var ex = await Assert.ThrowsAsync<ProcessExecutionResultException>(
            () => mpeg.ConvertToMp3(Input, Output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, ex.Result.ExitCode);
        Assert.Contains("No such file or directory", ex.Message);
        Assert.NotNull(ex.CommandLine);
    }

    [Fact]
    public async Task ConvertToMp3_ThrowsBeforeStartingFFmpeg_WhenInputIsMissing()
    {
        var (mpeg, process) = CreateMpegExecutor();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => mpeg.ConvertToMp3("./data/definitely-missing.wav", Output, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(process.Commands);
    }

    [Fact]
    public async Task ConvertToMp3_Throws_WhenInputAndOutputAreTheSameFile()
    {
        var (mpeg, process) = CreateMpegExecutor();

        await Assert.ThrowsAsync<ArgumentException>(
            () => mpeg.ConvertToMp3(Input, Input, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(process.Commands);
    }

    [Fact]
    public async Task GetChannelsAndSampleRate_SelectsTheFirstAudioStream()
    {
        // Регрессия: без -select_streams ffprobe печатает блок на каждый поток, и «последний
        // выигрывает» молча отдавал параметры чужой дорожки (например, видео, где 0 каналов).
        var process = new RecordingProcessExecutor { StandardOutput = "channels=2\nsample_rate=48000\n" };
        var probe = CreateProbeExecutor(process);

        var (channels, sampleRate) = await probe.GetChannelsAndSampleRate(
            Input, TestContext.Current.CancellationToken);

        var args = Args(process);
        Assert.Contains("-select_streams", args);
        Assert.Contains("a:0", args);
        Assert.Equal(2, channels);
        Assert.Equal(48000, sampleRate);
    }

    [Fact]
    public async Task GetMetaInfo_Throws_WhenFFprobeFails()
    {
        // Регрессия: стоял throwOnError: false, и на битом файле возвращался
        // MediaMetadata.Empty — вызывающий принимал это за «метаданных нет».
        var process = new RecordingProcessExecutor { ExitCode = 1, StandardError = "moov atom not found" };
        var probe = CreateProbeExecutor(process);

        var ex = await Assert.ThrowsAsync<ProcessExecutionResultException>(
            () => probe.GetMetaInfo(Input, TestContext.Current.CancellationToken));

        Assert.Contains("moov atom not found", ex.Message);
    }

    [Fact]
    public async Task GetMetaInfo_Throws_WhenFFprobeReturnedGarbageInsteadOfJson()
    {
        var process = new RecordingProcessExecutor { StandardOutput = "not json at all" };
        var probe = CreateProbeExecutor(process);

        await Assert.ThrowsAsync<ProcessExecutionException>(
            () => probe.GetMetaInfo(Input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetMetaInfo_ThrowsBeforeStartingFFprobe_WhenFileIsMissing()
    {
        var process = new RecordingProcessExecutor();
        var probe = CreateProbeExecutor(process);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => probe.GetMetaInfo("./data/definitely-missing.wav", TestContext.Current.CancellationToken));

        Assert.Empty(process.Commands);
    }
}
