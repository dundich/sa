using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// Provides extension methods for registering hybrid file storage services with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the hybrid file storage infrastructure with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure the storage container and interceptors.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the hybrid storage was already registered in this collection.</exception>
    /// <remarks>
    /// Registration is not additive: a second call would <c>TryAdd</c> the
    /// <see cref="IHybridFileStorage"/> factory into a collection that already has one, so the new
    /// factory — together with every storage and interceptor it configures — would be silently
    /// discarded. Throwing names the mistake instead.
    /// <para>
    /// The failover chain follows the registration order, i.e. the order of the provider
    /// <c>Add...</c> calls against the <see cref="IServiceCollection"/>: descriptors come first,
    /// storages added through <see cref="IHybridFileStorageConfiguration.ConfigureStorage"/> come
    /// after them, and the in-memory provider registered through the configuration pipeline takes
    /// the position of the <c>AddSaHybridFileStorage</c> call itself. The order can be overridden
    /// per basket with <see cref="IHybridFileStorageConfiguration.OrderTypes"/> and
    /// <see cref="IHybridFileStorageConfiguration.OrderBasketTypes"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaHybridFileStorage(
        this IServiceCollection services,
        Action<IHybridFileStorageConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(IHybridFileStorage)))
        {
            throw new InvalidOperationException(
                "AddSaHybridFileStorage has already been registered in this service collection. " +
                "A second call would be ignored, so the storages and interceptors configured by it " +
                "would be silently lost. Configure everything in a single call, or call it once and " +
                "register the providers with their own Add... methods.");
        }

        HybridStorageBuilder builder = new(services);
        configure?.Invoke(builder);
        builder.Build();
        return services;
    }

    /// <summary>
    /// Registers the in-memory file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Optional configuration options for the in-memory storage. If <c>null</c>, a default instance is used.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <remarks>
    /// The explicit instance is the "explicit instance wins" channel of the options priority
    /// (explicit instance → configuration section → defaults): it is used as-is, bypassing
    /// the pipeline, so nothing registered elsewhere can rewrite it.
    /// </remarks>
    public static IServiceCollection AddSaInMemoryFileStorage(
        this IServiceCollection services,
        InMemoryFileStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        InMemoryFileStorageOptions captured = options ?? new();

        RegisterInMemory(services, _ => captured);
        return services;
    }

    /// <summary>
    /// Registers the in-memory file storage provider from the standard options pipeline:
    /// a configuration section via <see cref="IInMemoryFileStorageBuilder.FromConfiguration"/>
    /// and pipeline actions via <see cref="IInMemoryFileStorageBuilder.Options"/>.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">The configuration channel: section and pipeline actions in one delegate.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="configure"/> is <c>null</c>.</exception>
    /// <remarks>
    /// The options materialise under a unique named instance (see the plan for the basket
    /// naming scheme), so several registrations never stack their <c>Configure</c> actions on
    /// one shared instance — each storage gets its own. The storage resolves the instance
    /// lazily through <c>IOptionsMonitor&lt;T&gt;.Get(name)</c>, which is also what makes a
    /// validation failure surface at the first read (or at host start, with
    /// <c>ValidateOnStart()</c> from the pipeline actions) instead of at registration.
    /// </remarks>
    public static IServiceCollection AddSaInMemoryFileStorage(
        this IServiceCollection services,
        Action<IInMemoryFileStorageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        RegisterInMemory(services, CreateInMemoryOptionsFactory(services, configure));
        return services;
    }

    /// <summary>
    /// Registers the in-memory file storage provider from a ready-made options instance,
    /// typically one the caller resolved from its own pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">The ready-made options; its <see cref="IOptions{T}.Value"/> is read once, at first use.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    public static IServiceCollection AddSaInMemoryFileStorage(
        this IServiceCollection services,
        IOptions<InMemoryFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        RegisterInMemory(services, _ => options.Value);
        return services;
    }

    /// <summary>
    /// Registers the in-memory file storage provider as part of the hybrid file storage configuration
    /// pipeline, enabling it to participate in the hybrid container and its interceptors.
    /// </summary>
    /// <param name="configuration">The hybrid file storage configuration pipeline.</param>
    /// <param name="options">Optional configuration options for the in-memory storage. If <c>null</c>, a default instance is used.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is <c>null</c>.</exception>
    /// <remarks>
    /// The provider is created by the container and registered as an <see cref="IFileStorage"/>,
    /// exactly as the service-collection overload does. It therefore appears in
    /// <c>sp.GetServices&lt;IFileStorage&gt;()</c> and is disposed with the provider, and the very
    /// same instance is what the hybrid container receives. Constructing it with <c>new</c> inside
    /// the deferred callback, as this overload used to, bypassed the container entirely.
    /// </remarks>
    public static IHybridFileStorageConfiguration AddSaInMemoryFileStorage(
        this IHybridFileStorageConfiguration configuration,
        InMemoryFileStorageOptions? options = null)
    {
        InMemoryFileStorageOptions captured = options ?? new();
        return AddInMemoryPipeline(configuration, services => RegisterInMemory(services, _ => captured));
    }

    /// <summary>
    /// Registers the in-memory file storage provider from the standard options pipeline as part
    /// of the hybrid configuration pipeline — the pipeline twin of the
    /// <see cref="AddSaInMemoryFileStorage(IServiceCollection, Action{IInMemoryFileStorageBuilder})"/>
    /// overload.
    /// </summary>
    /// <param name="configuration">The hybrid file storage configuration pipeline.</param>
    /// <param name="configure">The configuration channel: section and pipeline actions in one delegate.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> or <paramref name="configure"/> is <c>null</c>.</exception>
    public static IHybridFileStorageConfiguration AddSaInMemoryFileStorage(
        this IHybridFileStorageConfiguration configuration,
        Action<IInMemoryFileStorageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return AddInMemoryPipeline(
            configuration,
            services => RegisterInMemory(services, CreateInMemoryOptionsFactory(services, configure)));
    }

    /// <summary>
    /// Registers the in-memory file storage provider from a ready-made options instance as part
    /// of the hybrid configuration pipeline.
    /// </summary>
    /// <param name="configuration">The hybrid file storage configuration pipeline.</param>
    /// <param name="options">The ready-made options; its <see cref="IOptions{T}.Value"/> is read once, at first use.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> or <paramref name="options"/> is <c>null</c>.</exception>
    public static IHybridFileStorageConfiguration AddSaInMemoryFileStorage(
        this IHybridFileStorageConfiguration configuration,
        IOptions<InMemoryFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return AddInMemoryPipeline(configuration, services => RegisterInMemory(services, _ => options.Value));
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

    /// <summary>
    /// Builds the options factory for the pipeline channel: binds the section in the fixed
    /// slot, replays the caller's pipeline actions after it, and returns a reader for the
    /// named instance.
    /// </summary>
    private static Func<IServiceProvider, InMemoryFileStorageOptions> CreateInMemoryOptionsFactory(
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
    /// The one place that registers the TimeProvider and the <see cref="IFileStorage"/>
    /// descriptor for the in-memory provider — every channel differs only in how the
    /// options are read.
    /// </summary>
    private static void RegisterInMemory(
        IServiceCollection services, Func<IServiceProvider, InMemoryFileStorageOptions> options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStorage>(sp =>
            new InMemoryFileStorage(options(sp), sp.GetRequiredService<TimeProvider>()));
    }

    /// <summary>
    /// Shared wiring of the pipeline channel: the provider registers through
    /// <see cref="HybridStorageBuilder.ConfigureServices"/>, so it is part of
    /// <c>sp.GetServices&lt;IFileStorage&gt;()</c> when the container is built.
    /// </summary>
    private static IHybridFileStorageConfiguration AddInMemoryPipeline(
        IHybridFileStorageConfiguration configuration,
        Action<IServiceCollection> register)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // HybridStorageBuilder is the only implementation of the interface; the fallback keeps a
        // hypothetical external implementation working, at the cost of requiring the provider to
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
}
