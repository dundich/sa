using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Classes;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

public static class Setup
{
    public static IServiceCollection AddFFMpeg(this IServiceCollection services, FFMpegOptions? options = null)
    {
        services.TryAddTransient<IProcessExecutor, ProcessExecutor>();
        services.TryAddTransient<IFFMpegLocator, FFMpegLocator>();
        services.TryAddTransient<IFFMpegExecutorFactory, FFMpegExecutorFactory>();
        services.TryAddSingleton<IFFMpegExecutor>(sp => sp.GetRequiredService<IFFMpegExecutorFactory>().CreateFFMpegExecutor(options));
        services.TryAddSingleton<IFFProbeExecutor>(sp => sp.GetRequiredService<IFFMpegExecutorFactory>().CreateFFProbeExecutor(options));
        services.TryAddSingleton<IPcmS16LeChannelManipulator, PcmS16LeChannelManipulator>();

        return services;
    }
}
