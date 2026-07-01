using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

internal sealed class HybridStorageBuilder(IServiceCollection services) : IHybridFileStorageConfiguration
{
    private Action<IServiceProvider, HybridFileStorageContainerConfiguration>? _configureStorage;
    private Action<IServiceProvider, IInterceptorContainer>? _configureInterceptors;
    private bool _logged = false;

    public IHybridFileStorageConfiguration ConfigureStorage(
        Action<IServiceProvider, HybridFileStorageContainerConfiguration> configure)
    {
        _configureStorage = configure;
        return this;
    }

    public IHybridFileStorageConfiguration ConfigureInterceptors(
        Action<IServiceProvider, IInterceptorContainer> configure)
    {
        _configureInterceptors = configure;
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

            _configureInterceptors?.Invoke(sp, interceptorContainer);

            HybridFileStorageContainer storageContainer = new(sp.GetServices<IFileStorage>());

            var storageConfig = new HybridFileStorageContainerConfiguration(storageContainer.AddStorage);
            _configureStorage?.Invoke(sp, storageConfig);

            return new HybridFileStorage(storageContainer, interceptorContainer);
        });
    }
}
