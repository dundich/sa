using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

public static class Setup
{
    public static IServiceCollection AddSaFFMpeg(
        this IServiceCollection services,
        string? configSectionPath = null,
        Action<FFMpegOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optsBuilder = services.AddOptions<FFMpegOptions>();

        if (configSectionPath != null)
            optsBuilder.BindConfiguration(configSectionPath);

        optsBuilder
            .Configure(configure ?? (_ => { }))
            // Явные проверки вместо ValidateDataAnnotations(): тот помечен
            // RequiresUnreferencedCode (IL2026) и ломает Native AOT, ради которого эта сборка
            // и существует. Набор атрибутов тут всё равно минимален.
            // Сообщение Validate() не поддерживает подстановку значений (только {Key}),
            // поэтому оно статическое.
            .Validate(
                static o => o.TimeoutSeconds is null or >= 0,
                "FFMpegOptions:TimeoutSeconds must be non-negative or left unset.")
            // Каталог создаёт фабрика (EnsureWritableDirectory), так что его отсутствие — не
            // ошибка настройки; настоящая ошибка — путь, указывающий на *файл*, в этом случае
            // CreateDirectory роняет непонятное IOException при первом resolve.
            .Validate(
                static o => o.WritableDirectory is null || !File.Exists(o.WritableDirectory),
                "FFMpegOptions:WritableDirectory points to a file, not a directory. " +
                "Leave the option unset to use the default, or point it at a directory (it is created if missing).")
            .ValidateOnStart();

        // Singleton, а не Transient: FFMpegLocator кэширует найденный путь, и пересоздавать его
        // на каждый resolve — значит заново обходить диск. Оба объекта потокобезопасны
        // (у ProcessExecutor состояние — только логгер).
        services.TryAddSingleton<IProcessExecutor, ProcessExecutor>();
        services.TryAddSingleton<IFFMpegLocator, FFMpegLocator>();

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
