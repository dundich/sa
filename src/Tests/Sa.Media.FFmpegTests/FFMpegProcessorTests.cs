using Sa.Media.FFmpeg;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

public sealed class FFMpegProcessorTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    static IFFMpegExecutor Processor => IFFMpegExecutor.Default;


    [Fact]
    public async Task GetVersion_ShouldNotEmptyGetVersion()
    {
        // Act
        var r = await Processor.GetVersion(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task GetFormats_ShouldNotEmpty()
    {
        // Act
        var r = await Processor.GetFormats(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Fact]
    public async Task GetCodecs_ShouldNotEmpty()
    {
        // Act
        var r = await Processor.GetCodecs(CancellationToken);
        Assert.NotEmpty(r);
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/gsm.wav")]
    public async Task ConvertToPcm16Wav_CallsFFmpegWithCorrectArguments(string testFilePath)
    {
        // Act
        await Processor.ConvertToPcmS16Le(
            testFilePath, "./data/output.wav", isOverwrite: true, cancellationToken: CancellationToken);
        Assert.True(File.Exists("./data/output.wav"));
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/gsm.wav")]
    [InlineData("./data/12345.wav")]
    public async Task ConvertToPcm16Wav_CallsFFmpegAsStream(string testFilePath)
    {
        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        using var fileStream = new MemoryStream();
        // new FileStream("./data/output.wav", FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);

        // Act
        await Processor.ConvertToPcmS16Le(inputStream, ext, async (outStream, _) =>
        {
            await outStream.CopyToAsync(fileStream, CancellationToken);
            await fileStream.FlushAsync(CancellationToken);
        }, cancellationToken: CancellationToken);

        Assert.True(fileStream.Length > 0);

        fileStream.Position = 0;
        var info = await IFFProbeExecutor.Default.GetMetaInfo(fileStream, "wav", CancellationToken);
        Assert.Equal("wav", info.FormatName);
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToPcmS16Le_WhenFfmpegFails_ThrowsException(string testFilePath)
    {
        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        // Act
        var ex = await Assert.ThrowsAsync<ProcessExecutionException>(async () =>
        {
            await Processor.ConvertToPcmS16Le(inputStream, ext,
                (_, __) => Task.CompletedTask,
                cancellationToken: CancellationToken);
        });

        Assert.NotNull(ex);
    }


    [Theory]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToPcmS16Le_WhenOutputCallbackThrows_ExceptionIsPropagated(string testFilePath)
    {
        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await Processor.ConvertToPcmS16Le(inputStream, ext,
                (_, __) => throw new Exception("test"),
                cancellationToken: CancellationToken);
        });

        Assert.Equal("test", ex.Message);
    }


    [Fact]
    public async Task ConvertToPcmS16Le_WhenFmtInvalid_ExceptionIsPropagated()
    {
        await Assert.ThrowsAsync<ProcessExecutionException>(async () =>
        {
            await Processor.ConvertToPcmS16Le(new MemoryStream(),
                "wav",
                (_, __) => Task.CompletedTask,
                cancellationToken: CancellationToken);
        });
    }


    [Fact]
    public async Task ConvertToPcmS16Le_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var inputStream = new MemoryStream(new byte[1024]); // небольшой валидный поток (или имитация)
        await cts.CancelAsync(); // сразу отменяем — чтобы проверить реакцию

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await Processor.ConvertToPcmS16Le(
                inputStream: inputStream,
                inputFormat: "mp3",
                onOutput: (_, __) => Task.CompletedTask,
                cancellationToken: cts.Token);
        });
    }


    [Theory]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToPcmS16Le_WhenCancelledDuringExecution_ThrowsOperationCanceledException(string testFilePath)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        using var inputStream = File.OpenRead(testFilePath);

        var task = Processor.ConvertToPcmS16Le(
            inputStream: inputStream,
            inputFormat: "mp3",
            onOutput: async (output, ct) =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var read = await output.ReadAsync(buffer, ct);
                        if (read == 0) break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            },
            cancellationToken: cts.Token);

        await Task.Delay(10, CancellationToken);
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }


    [Theory]
    [InlineData("./data/input.ogg")]
    [InlineData("./data/input.wav")]
    public async Task ConvertToMp3_ShouldBeWork(string testFilePath)
    {
        var fn = "./data/output.mp3";
        // Act
        await Processor.ConvertToMp3(
            testFilePath, fn, isOverwrite: true, cancellationToken: CancellationToken);
        Assert.True(File.Exists(fn));

        FileInfo fileInfo = new(fn);
        Assert.True(fileInfo.Length > 5000);
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    [InlineData("./data/input.wav")]
    public async Task ConvertToOgg_ShouldBeWork(string testFilePath)
    {
        var fn = "./data/output.ogg_";
        // Act
        await Processor.ConvertToOgg(
            testFilePath, fn, isOverwrite: true, cancellationToken: CancellationToken);
        Assert.True(File.Exists(fn));
    }


    [Theory]
    [InlineData("./data/input.ogg")]
    [InlineData("./data/input.wav")]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToMono_ShouldProduceValidMonoFile(string inputPath)
    {
        // Arrange
        string outputPath = Path.ChangeExtension(inputPath, ".pcm");

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        await Processor.ConvertToPcmS16Le(
            inputFileName: inputPath,
            outputFileName: outputPath,
            isOverwrite: true,
            outputChannelCount: 1,
            outputSampleRate: 8000,
            cancellationToken: CancellationToken
        );

        Assert.True(File.Exists(outputPath));

        var ffprobe = CreateFFProbeExecutor();
        var (channels, sampleRate) = await ffprobe.GetChannelsAndSampleRate(outputPath, cancellationToken: CancellationToken);

        Assert.Equal(1, channels);
        Assert.Equal(8000, sampleRate);
    }

    [Theory]
    [InlineData("./data/input.wav")]
    public async Task ConvertToPcmS16LePreservingFormat_ShouldPreserveOriginalSettings(string inputPath)
    {
        // Arrange
        string outputPath = "./data/output_preserved.wav";

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // Получаем исходные настройки
        var originalInfo = await IFFProbeExecutor.Default.GetMetaInfo(inputPath, CancellationToken);
        var (origChannels, origSampleRate) = await IFFProbeExecutor.Default.GetChannelsAndSampleRate(inputPath, CancellationToken);

        // Act
        await Processor.ConvertToPcmS16LePreservingFormat(
            inputFileName: inputPath,
            outputFileName: outputPath,
            isOverwrite: true,
            cancellationToken: CancellationToken);

        // Assert
        Assert.True(File.Exists(outputPath));

        var ffprobe = CreateFFProbeExecutor();
        var (outChannels, outSampleRate) = await ffprobe.GetChannelsAndSampleRate(outputPath, cancellationToken: CancellationToken);

        Assert.Equal(origChannels, outChannels);
        Assert.Equal(origSampleRate, outSampleRate);
    }

    [Theory]
    [InlineData("./data/input.wav")]
    public async Task ConvertToPcmS32Le_ShouldProduceValidFloat32File(string inputPath)
    {
        // Arrange
        string outputPath = "./data/output_pcmf32le.wav";

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // Act
        await Processor.ConvertToPcmS32Le(
            inputFileName: inputPath,
            outputFileName: outputPath,
            outputSampleRate: 48000,
            outputChannelCount: 2,
            isOverwrite: true,
            cancellationToken: CancellationToken);

        // Assert
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 0);

        var ffprobe = CreateFFProbeExecutor();
        var (channels, sampleRate) = await ffprobe.GetChannelsAndSampleRate(outputPath, cancellationToken: CancellationToken);

        Assert.Equal(2, channels);
        Assert.Equal(48000, sampleRate);
    }

    [Theory]
    [InlineData("./data/input.wav")]
    public async Task ConvertToPcmF32LeRaw_ShouldProduceValidRawFile(string inputPath)
    {
        // Arrange
        string outputPath = "./data/output_rawf32le.f32le";

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // Act
        await Processor.ConvertToPcmF32LeRaw(
            inputFileName: inputPath,
            outputFileName: outputPath,
            outputSampleRate: 48000,
            outputChannelCount: 2,
            isOverwrite: true,
            cancellationToken: CancellationToken);

        // Assert
        Assert.True(File.Exists(outputPath));
        var fileInfo = new FileInfo(outputPath);
        Assert.True(fileInfo.Length > 0);

        // Raw f32le: each sample = 4 bytes (float), 2 channels => 8 bytes per frame
        // duration ≈ file.Length / (4 * 2 * 48000)
        Assert.True(fileInfo.Length % (4 * 2) == 0, "Raw f32le file size should be divisible by (4 bytes × 2 channels)");
    }

    [Theory]
    [InlineData("./data/input.mp3")]
    public async Task ConvertToPcmF32LeRaw_Stream_ShouldProduceData(string testFilePath)
    {
        // Arrange
        var ext = Path.GetExtension(testFilePath).TrimStart('.');

        using var inputStream = File.OpenRead(testFilePath);

        // Act
        var rawBytes = new MemoryStream();
        await Processor.ConvertToPcmF32LeRaw(inputStream, ext,
            async (outStream, _) =>
            {
                await outStream.CopyToAsync(rawBytes, CancellationToken);
            },
            outputSampleRate: 16000,
            outputChannelCount: 1,
            cancellationToken: CancellationToken);

        // Assert
        Assert.True(rawBytes.Length > 0, "Raw f32le stream should produce data");
        Assert.True(rawBytes.Length % 4 == 0, "Raw f32le bytes should be divisible by 4 (float size)");
    }

    private static IFFProbeExecutor CreateFFProbeExecutor()
    {
        return IFFProbeExecutor.Default;
    }
}
