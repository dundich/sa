using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.FileSystem;


public static class Setup
{
    public static IServiceCollection AddSaFileSystemFileStorage(
        this IServiceCollection services,
        FileSystemStorageOptions options)
    {
        services.AddSingleton<IFileStorage>(sp => new FileSystemStorage(options, sp.GetService<TimeProvider>()));

        return services;
    }
}
