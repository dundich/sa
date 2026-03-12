using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Classes;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

public static class Setup
{
    public static IServiceCollection AddSaFFMpeg(
        this IServiceCollection services,
        Action<FFMpegOptions>? configure = null)
    {
        services.AddOptions<FFMpegOptions>()
            .Configure(configure ?? (_ => { }))
            .PostConfigure(options => options.Validate())
            .ValidateOnStart();

        services.TryAddTransient<IProcessExecutor, ProcessExecutor>();
        services.TryAddTransient<IFFMpegLocator, FFMpegLocator>();

        services.TryAddSingleton<IPcmS16LeChannelManipulator, PcmS16LeChannelManipulator>();

        services.TryAddSingleton<IFFMpegExecutorFactory, FFMpegExecutorFactory>();

        services.TryAddSingleton<IFFMpegExecutor>(sp =>
        {
            var factory = sp.GetRequiredService<IFFMpegExecutorFactory>();
            var options = sp.GetRequiredService<IOptions<FFMpegOptions>>().Value;
            return factory.CreateFFMpegExecutor(options);
        });

        services.TryAddSingleton<IFFProbeExecutor>(sp =>
        {
            var factory = sp.GetRequiredService<IFFMpegExecutorFactory>();
            var options = sp.GetRequiredService<IOptions<FFMpegOptions>>().Value;
            return factory.CreateFFProbeExecutor(options);
        });

        return services;
    }
}
