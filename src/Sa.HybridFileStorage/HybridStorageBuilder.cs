using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

internal sealed class HybridStorageBuilder(IServiceCollection services) : IHybridFileStorageConfiguration
{
    private Action<IServiceProvider, IHybridFileStorageContainerConfiguration>? _configureStorage;
    private Action<IServiceProvider, IInterceptorContainer>? _configureInterceptors;

    public IHybridFileStorageConfiguration ConfigureStorage(Action<IServiceProvider, IHybridFileStorageContainerConfiguration> configure)
    {
        _configureStorage = configure;
        return this;
    }

    public IHybridFileStorageConfiguration ConfigureInterceptors(Action<IServiceProvider, IInterceptorContainer> configure)
    {
        _configureInterceptors = configure;
        return this;
    }

    public IHybridFileStorageConfiguration AddLogging()
    {
        services.TryAddTransient<LoggingInterceptor>();
        return this;
    }

    public void Build()
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        services.TryAddSingleton<IHybridFileStorage>(sp =>
        {

            var interceptorContainer = new InterceptorContainer();
            interceptorContainer.AddLoggingInterceptor(sp.GetService<LoggingInterceptor>());

            _configureInterceptors?.Invoke(sp, interceptorContainer);


            IHybridFileStorageContainer storageContainer = sp.GetService<IHybridFileStorageContainer>()
                ?? new HybridFileStorageContainer(sp.GetServices<IFileStorage>());

            _configureStorage?.Invoke(sp, storageContainer);


            return new HybridFileStorage(storageContainer, interceptorContainer);
        });
    }
}
