using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

internal sealed class HybridStorageBuilder(IServiceCollection services) : IHybridFileStorageConfiguration
{
    private readonly List<Action<IServiceProvider, HybridFileStorageContainerConfiguration>> _configureStorages = [];
    private readonly List<Action<IServiceProvider, IInterceptorContainer>> _configureInterceptors = [];
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

            return new HybridFileStorage(storageContainer, interceptorContainer);
        });
    }
}
