using Microsoft.Extensions.DependencyInjection;
using Sa.Media.FFmpeg;

namespace Sa.Media.FFmpegTests;

public class DependencyInjectionTests
{
    [Fact]
    public void Services_ShouldBeRegisteredCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddFFMpeg();

        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var ffmpegExecutor = serviceProvider.GetService<IFFMpegExecutor>();
        Assert.NotNull(ffmpegExecutor);

        var ffprobeExecutor = serviceProvider.GetService<IFFProbeExecutor>();
        Assert.NotNull(ffprobeExecutor);
    }
}