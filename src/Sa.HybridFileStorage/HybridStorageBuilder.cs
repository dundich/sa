using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

internal sealed class HybridStorageBuilder(IServiceCollection services) : IHybridFileStorageConfiguration
{
    private Action<IServiceProvider, IHybridFileStorageContainerConfiguration>? _configureStorage;
    private Action<IServiceProvider, IInterceptorContainer>? _configureInterceptors;
    private bool _logged = false;

    public IHybridFileStorageConfiguration ConfigureStorage(
        Action<IServiceProvider, IHybridFileStorageContainerConfiguration> configure)
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
            services.TryAddSingleton<LoggingInterceptor>();
        }

        services.TryAddSingleton<IHybridFileStorage>(sp =>
        {
            InterceptorContainer interceptorContainer = new();
            interceptorContainer.AddLoggingInterceptor(sp.GetService<LoggingInterceptor>());

            _configureInterceptors?.Invoke(sp, interceptorContainer);

            HybridFileStorageContainer storageContainer = new(sp.GetServices<IFileStorage>());

            _configureStorage?.Invoke(sp, storageContainer);

            return new HybridFileStorage(storageContainer, interceptorContainer);
        });
    }
}
