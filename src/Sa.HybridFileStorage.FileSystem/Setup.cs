using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.FileSystem;


public static class Setup
{
    public static IServiceCollection AddFileSystemFileStorage(this IServiceCollection services, FileSystemStorageOptions options)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddKeyedSingleton<FileSystemStorageOptions>(options);
        services.TryAddKeyedSingleton<FileSystemStorage>(options, (sp, o) => new FileSystemStorage(options, sp.GetService<TimeProvider>()));
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredKeyedService<FileSystemStorage>(options));

        return services;
    }
}
