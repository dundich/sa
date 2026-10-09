using Microsoft.Extensions.DependencyInjection;
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
        => HybridStorageRegistrar.Register(services, options);

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
        => HybridStorageRegistrar.Register(services, configure);

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
        => HybridStorageRegistrar.Register(services, options);

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
        => HybridStorageRegistrar.RegisterPipeline(configuration, options);

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
        => HybridStorageRegistrar.RegisterPipeline(configuration, configure);

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
        => HybridStorageRegistrar.RegisterPipeline(configuration, options);
}
