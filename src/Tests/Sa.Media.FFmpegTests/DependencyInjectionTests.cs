using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Media.FFmpeg;

namespace Sa.Media.FFmpegTests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void Services_ShouldBeRegisteredCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddSaFFMpeg();

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var ffmpegExecutor = serviceProvider.GetService<IFFMpegExecutor>();
        Assert.NotNull(ffmpegExecutor);

        var ffprobeExecutor = serviceProvider.GetService<IFFProbeExecutor>();
        Assert.NotNull(ffprobeExecutor);
    }

    [Fact]
    public void NegativeTimeoutSeconds_FailsValidationOnResolve()
    {
        // ValidateOnStart: неверная настройка должна проявиться при первом обращении к
        // опциям (старт хоста / первый resolve), а не посреди конвертации.
        var services = new ServiceCollection();
        services.AddSaFFMpeg(o => o.Options(ob => ob.Configure(x => x.TimeoutSeconds = -1)));

        var serviceProvider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => serviceProvider.GetRequiredService<IFFMpegExecutor>());

        Assert.Contains("TimeoutSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritableDirectory_IsCreatedOnResolve()
    {
        // «Создаётся, если отсутствует» (FFMpegOptions): валидация не требует наличия
        // каталога (его создаёт фабрика), а только что он не является файлом.
        var dir = Path.Combine(Path.GetTempPath(), "sa-ffmpeg-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSaFFMpeg(o => o.Options(ob => ob.Configure(x => x.WritableDirectory = dir)));

            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetRequiredService<IFFMpegExecutor>();

            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WritableDirectoryPointingToFile_FailsValidation()
    {
        // Путь на существующий *файл* — единственная реальная ошибка настройки:
        // CreateDirectory в фабрике ронял бы там непонятное IOException.
        var file = Path.Combine(Path.GetTempPath(), "sa-ffmpeg-di-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(file, "not a directory");

            var services = new ServiceCollection();
            services.AddSaFFMpeg(o => o.Options(ob => ob.Configure(x => x.WritableDirectory = file)));

            var serviceProvider = services.BuildServiceProvider();

            var ex = Assert.Throws<OptionsValidationException>(
                () => serviceProvider.GetRequiredService<IFFMpegExecutor>());

            Assert.Contains("WritableDirectory", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Fact]
    public async Task WritableDirectory_IsPropagatedToTheChannelManipulator()
    {
        // Манипулятор получает WritableDirectory из тех же опций, что и исполнители: без этого
        // SplitAsync/JoinAsync (raw-executor) писали в CWD, пока ConvertToPcm* — в WritableDirectory.
        var dir = Path.Combine(Path.GetTempPath(), "sa-ffmpeg-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSaFFMpeg(o => o.Options(ob => ob.Configure(x => x.WritableDirectory = dir)));

            using var serviceProvider = services.BuildServiceProvider();
            var manipulator = serviceProvider.GetRequiredService<IPcmS16LeChannelManipulator>();

            IReadOnlyList<string> files = await manipulator.SplitAsync(
                Path.GetFullPath("./data/input.mp3"),
                "bare.wav",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, files.Count);
            Assert.All(files, file =>
            {
                Assert.StartsWith(Path.GetFullPath(dir), file, StringComparison.Ordinal);
                Assert.True(File.Exists(file), $"Ожидался файл {file}");
            });
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
