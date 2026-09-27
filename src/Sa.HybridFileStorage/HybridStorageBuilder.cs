using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

internal sealed class HybridStorageBuilder(IServiceCollection services) : IHybridFileStorageConfiguration
{
    private readonly List<Action<IServiceProvider, HybridFileStorageContainerConfiguration>> _configureStorages = [];
    private readonly List<Action<IServiceProvider, IInterceptorContainer>> _configureInterceptors = [];
    private readonly List<Action<IServiceCollection>> _configureServices = [];
    private bool _logged = false;

    public IHybridFileStorageConfiguration ConfigureStorage(
        Action<IServiceProvider, HybridFileStorageContainerConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configureStorages.Add(configure);
        return this;
    }

    public IHybridFileStorageConfiguration ConfigureInterceptors(
        Action<IServiceProvider, IInterceptorContainer> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configureInterceptors.Add(configure);
        return this;
    }

    /// <summary>
    /// Queues registrations that must be applied to the service collection eagerly, before the
    /// container is built — needed by a provider that must appear in
    /// <c>sp.GetServices&lt;IFileStorage&gt;()</c> rather than only in the container.
    /// </summary>
    public void ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configureServices.Add(configure);
    }

    public IHybridFileStorageConfiguration AddLogging()
    {
        _logged = true;
        return this;
    }

    public void Build()
    {
        if (_logged)
        {
            services.TryAddSingleton<UploadLoggingInterceptor>();
            services.TryAddSingleton<DownloadLoggingInterceptor>();
            services.TryAddSingleton<DeleteLoggingInterceptor>();
        }

        // Applied before the IHybridFileStorage factory is added, so a provider registered here is
        // already part of the collection when the factory runs at resolve time.
        foreach (var configure in _configureServices)
        {
            configure(services);
        }

        services.TryAddSingleton<IHybridFileStorage>(sp =>
        {
            InterceptorContainer interceptorContainer = new();
            interceptorContainer.AddLoggingInterceptors(
                sp.GetService<UploadLoggingInterceptor>(),
                sp.GetService<DownloadLoggingInterceptor>(),
                sp.GetService<DeleteLoggingInterceptor>());

            foreach (var configure in _configureInterceptors)
            {
                configure(sp, interceptorContainer);
            }

            HybridFileStorageContainer storageContainer = new(sp.GetServices<IFileStorage>());

            var storageConfig = new HybridFileStorageContainerConfiguration(storageContainer.AddStorage);
            foreach (var configure in _configureStorages)
            {
                configure(sp, storageConfig);
            }

            // A container with no storage resolves successfully and then fails every operation
            // with HybridFileStorageNoAvailableException, which points at the call site rather
            // than at the missing registration. Fail here instead, where the cause is visible.
            if (!storageContainer.Storages.Any())
            {
                throw new InvalidOperationException(
                    "No IFileStorage provider is available to the hybrid container. " +
                    "Register at least one provider — for example " +
                    "services.AddSaInMemoryFileStorage() or services.AddSaFileSystemFileStorage(...) — " +
                    "or add one explicitly via IHybridFileStorageConfiguration.ConfigureStorage.");
            }

            return new HybridFileStorage(storageContainer, interceptorContainer);
        });
    }
}
