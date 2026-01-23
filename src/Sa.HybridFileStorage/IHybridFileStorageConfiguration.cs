using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageConfiguration
{
    IHybridFileStorageConfiguration ConfigureInterceptors(Action<IServiceProvider, IInterceptorContainer> configure);
    IHybridFileStorageConfiguration ConfigureStorage(Action<IServiceProvider, IHybridFileStorageContainerConfiguration> configure);
    IHybridFileStorageConfiguration AddLogging();
}
