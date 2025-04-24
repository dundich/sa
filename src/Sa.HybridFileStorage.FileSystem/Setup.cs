using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.FileSystem;


public static class Setup
{
    public static IServiceCollection AddFileSystemFileStorage(this IServiceCollection services, FileSystemStorageOptions options)
    {
        services.AddSaInfrastructure();
        services.TryAddKeyedSingleton<FileSystemStorageOptions>(options);
        services.TryAddKeyedSingleton<FileSystemStorage>(options, (sp, o) => new FileSystemStorage(options, sp.GetRequiredService<ICurrentTimeProvider>()));
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredKeyedService<FileSystemStorage>(options));

        return services;
    }
}
