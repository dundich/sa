using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpeg;

public static class Setup
{
    /// <summary>
    /// Регистрирует FFmpeg поверх стандартного конвейера <c>Microsoft.Extensions.Options</c>.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configure">
    /// Единственный канал конфигурации: секция через <see cref="IFFMpegBuilder.FromConfiguration"/>
    /// и стандартный конвейер (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) через
    /// <see cref="IFFMpegBuilder.Options"/> — в одном делегате. Вызывается один раз, сразу;
    /// его действия <c>Options(...)</c> воспроизводятся после собственных регистраций метода,
    /// поэтому <c>Configure</c> отрабатывает последним, а <c>Validate</c> добавляется к
    /// встроенным проверкам, а не заменяет их.
    /// </param>
    /// <returns>Та же <see cref="IServiceCollection"/> с добавленными сервисами.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> — <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// FFmpeg уже зарегистрирован в этой коллекции.
    /// </exception>
    /// <remarks>
    /// Порядок конвейера фиксирован: привязка секции (<c>FromConfiguration</c>, «сырые»
    /// значения) и затем <c>Configure</c> из <c>Options(...)</c> →
    /// <c>PostConfigure</c> (нормализация) и затем <c>PostConfigure</c> из
    /// <c>Options(...)</c> → валидация. Поэтому валидация видит уже нормализованные значения,
    /// а <c>ValidateOnStart()</c> превращает неверную настройку в
    /// <see cref="OptionsValidationException"/> на старте хоста вместо ошибки посреди
    /// конвертации.
    /// </remarks>
    public static IServiceCollection AddSaFFMpeg(
        this IServiceCollection services,
        Action<IFFMpegBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Владелец безымянного FFMpegOptions. Второй вызов добавил бы ещё один
        // IConfigureOptions в тот же экземпляр, и обе Configure-функции применились бы — настройки
        // молча слились бы. Падаем здесь, где причина видна (как в AddSaFileSystemFileStorage).
        if (services.Any(d => d.ServiceType == typeof(FFMpegRegistration)))
        {
            throw new InvalidOperationException(
                "AddSaFFMpeg has already been registered in this service collection. " +
                "The second call would add another IConfigureOptions over the same options instance, " +
                "and both Configure callbacks would apply, so the settings would silently merge. " +
                "Register FFmpeg only once per service collection.");
        }

        services.AddSingleton(new FFMpegRegistration());

        var optsBuilder = services.AddOptions<FFMpegOptions>();

        FFMpegBuilder? builder = null;

        if (configure is not null)
        {
            builder = new FFMpegBuilder();
            configure(builder);
        }

        // Фиксированный слот: секция биндится после того, как колбэк её записал, но до
        // воспроизведения его действий Options(...) — где бы они ни стояли в колбэке.
        var sectionPath = builder?.ConfigSectionPath;

        if (sectionPath is not null)
        {
            optsBuilder.BindConfiguration(sectionPath);
        }

        // Post-инициализация: пути приводятся к единому виду до того, как их увидят валидация и
        // фабрика. Проверка на IsNullOrWhiteSpace обязательна: Path.GetFullPath("   ") на Unix
        // успешно возвращает путь (пробелы — легальное имя файла), так что без неё пустая опция
        // превратилась бы в каталог, который никто не просил создавать.
        optsBuilder.PostConfigure(static options =>
        {
            options.ExecutablePath = NormalizePath(options.ExecutablePath);
            options.WritableDirectory = NormalizePath(options.WritableDirectory);
        });

        optsBuilder.ValidateOnStart();

        // IValidateOptions, а не ValidateDataAnnotations(): тот помечен RequiresUnreferencedCode
        // (IL2026) и ломает Native AOT, ради которого эта сборка и существует.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FFMpegOptions>, FFMpegOptionsValidator>());

        // Воспроизводятся в этом слоте — после привязки секции и после наших
        // PostConfigure/Validate, поэтому Configure пользователя перебивает секцию, а его
        // PostConfigure и Validate идут после наших.
        if (builder is { SettingsActions.Count: > 0 })
        {
            foreach (var settingsAction in builder.SettingsActions)
            {
                settingsAction(optsBuilder);
            }
        }

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

    // Хелпер, а не локальная функция внутри PostConfigure: он нужен дважды, а локальная функция в
    // статическом лямбде тащит за собой замыкание — здесь оно всё равно лишнее.
    static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Path.GetFullPath(path.Trim());
    }
}

/// <summary>
/// Маркер того, что FFmpeg уже зарегистрирован в этой коллекции, чтобы второй вызов
/// <see cref="Setup.AddSaFFMpeg"/> падал сразу, а не молча сливал настройки.
/// </summary>
internal sealed class FFMpegRegistration
{
}