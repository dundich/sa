using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// The central registration point for file storage in the hybrid file storage package: every public
/// <c>AddSaInMemoryFileStorage</c> overload in <see cref="Setup"/> delegates here. The in-memory
/// provider is the storage this package registers out of the box — a reference registration; the
/// other providers (S3, Postgres, filesystem) register through their own package's registrar, and
/// each channel here differs only in how the storage's options are read.
/// </summary>
internal static class HybridStorageRegistrar
{
    /// <summary>
    /// Registers the storage against the service collection from an explicit options
    /// instance (the "explicit instance wins" channel of the options priority: explicit
    /// instance → configuration section → defaults).
    /// </summary>
    public static IServiceCollection Register(
        IServiceCollection services,
        InMemoryFileStorageOptions? options)
    {
        ArgumentNullException.ThrowIfNull(services);

        InMemoryFileStorageOptions captured = options ?? new();

        RegisterCore(services, _ => captured);
        return services;
    }

    /// <summary>
    /// Registers the storage against the service collection from the standard options
    /// pipeline: a configuration section via <see cref="IInMemoryFileStorageBuilder.FromConfiguration"/>
    /// and pipeline actions via <see cref="IInMemoryFileStorageBuilder.Options"/>.
    /// </summary>
    public static IServiceCollection Register(
        IServiceCollection services,
        Action<IInMemoryFileStorageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        RegisterCore(services, CreateOptionsFactory(services, configure));
        return services;
    }

    /// <summary>
    /// Registers the storage against the service collection from a ready-made options
    /// instance, typically one the caller resolved from its own pipeline; its
    /// <see cref="IOptions{T}.Value"/> is read once, at first use.
    /// </summary>
    public static IServiceCollection Register(
        IServiceCollection services,
        IOptions<InMemoryFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        RegisterCore(services, _ => options.Value);
        return services;
    }

    /// <summary>
    /// Registers the storage as part of the hybrid file storage configuration pipeline
    /// from an explicit options instance, enabling it to participate in the hybrid
    /// container and its interceptors.
    /// </summary>
    public static IHybridFileStorageConfiguration RegisterPipeline(
        IHybridFileStorageConfiguration configuration,
        InMemoryFileStorageOptions? options)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        InMemoryFileStorageOptions captured = options ?? new();
        return AttachToPipeline(configuration, services => RegisterCore(services, _ => captured));
    }

    /// <summary>
    /// Registers the storage as part of the hybrid file storage configuration pipeline
    /// from the standard options pipeline — the pipeline twin of
    /// <see cref="Register(IServiceCollection, Action{IInMemoryFileStorageBuilder})"/>.
    /// </summary>
    public static IHybridFileStorageConfiguration RegisterPipeline(
        IHybridFileStorageConfiguration configuration,
        Action<IInMemoryFileStorageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        return AttachToPipeline(
            configuration,
            services => RegisterCore(services, CreateOptionsFactory(services, configure)));
    }

    /// <summary>
    /// Registers the storage as part of the hybrid file storage configuration pipeline
    /// from a ready-made options instance; its <see cref="IOptions{T}.Value"/> is read once,
    /// at first use.
    /// </summary>
    public static IHybridFileStorageConfiguration RegisterPipeline(
        IHybridFileStorageConfiguration configuration,
        IOptions<InMemoryFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        return AttachToPipeline(configuration, services => RegisterCore(services, _ => options.Value));
    }

    /// <summary>
    /// The one place that registers the TimeProvider and the <see cref="IFileStorage"/>
    /// descriptor for the storage — every channel differs only in how the options are read.
    /// </summary>
    private static void RegisterCore(
        IServiceCollection services, Func<IServiceProvider, InMemoryFileStorageOptions> options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStorage>(sp =>
            new InMemoryFileStorage(options(sp), sp.GetRequiredService<TimeProvider>()));
    }

    /// <summary>
    /// Shared wiring of the pipeline channel: the storage registers through
    /// <see cref="HybridStorageBuilder.ConfigureServices"/>, so it is part of
    /// <c>sp.GetServices&lt;IFileStorage&gt;()</c> when the container is built.
    /// </summary>
    private static IHybridFileStorageConfiguration AttachToPipeline(
        IHybridFileStorageConfiguration configuration,
        Action<IServiceCollection> register)
    {
        // HybridStorageBuilder is the only implementation of the interface; the fallback keeps a
        // hypothetical external implementation working, at the cost of requiring the storage to
        // have been registered separately as an IFileStorage.
        if (configuration is HybridStorageBuilder builder)
        {
            builder.ConfigureServices(register);

            // Adding the resolved instance is a no-op when the container has already picked it up
            // from sp.GetServices<IFileStorage>() — HybridFileStorageContainer de-duplicates by
            // reference — and it keeps the pipeline's intent explicit.
            return builder.ConfigureStorage((sp, container) =>
                container.AddStorage(sp.GetServices<IFileStorage>()
                    .First(s => s is InMemoryFileStorage)));
        }

        return configuration.ConfigureStorage((sp, container) =>
            container.AddStorage(sp.GetRequiredService<IFileStorage>()));
    }

    /// <summary>
    /// Builds the options factory for the pipeline channel: binds the section in the fixed
    /// slot, replays the caller's pipeline actions after it, and returns a reader for the
    /// named instance.
    /// </summary>
    private static Func<IServiceProvider, InMemoryFileStorageOptions> CreateOptionsFactory(
        IServiceCollection services, Action<IInMemoryFileStorageBuilder> configure)
    {
        InMemoryFileStorageBuilder storageBuilder = new();
        configure(storageBuilder);

        string optionsName = NextOptionsName(storageBuilder.ConfigSectionPath);
        OptionsBuilder<InMemoryFileStorageOptions> builder =
            services.AddOptions<InMemoryFileStorageOptions>(optionsName);

        // Fixed slot: the section binds after the callback has recorded it, before its
        // Options(...) actions replay — wherever those calls sit in the callback.
        if (storageBuilder.ConfigSectionPath is { } sectionPath)
        {
            builder.BindConfiguration(sectionPath);
        }

        foreach (var settingsAction in storageBuilder.SettingsActions)
        {
            settingsAction(builder);
        }

        return sp => sp.GetRequiredService<IOptionsMonitor<InMemoryFileStorageOptions>>().Get(optionsName);
    }

    /// <summary>
    /// Sequence for unique options-instance names within this assembly — one registration,
    /// one named instance. See the plan for the naming scheme (unique instance name).
    /// </summary>
    private static int s_optionsSequence;

    /// <summary>
    /// Names this registration's options instance: the section path when one was given
    /// (readable in diagnostics), the provider label otherwise, plus a sequence number that
    /// makes the name unique per registration. The name is an internal detail — tests read
    /// it back through the registration markers, never by hardcoding it.
    /// </summary>
    private static string NextOptionsName(string? sectionPath)
        => $"{sectionPath ?? "InMemoryFileStorage"}#{Interlocked.Increment(ref s_optionsSequence)}";
}
