using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.FileSystemStorage;


public static class Setup
{
    public static IServiceCollection AddFileSystemFileStorage(this IServiceCollection services, FileSystemStorageOptions options)
    {
        services.TryAddKeyedSingleton<FileSystemStorageOptions>(options);
        services.TryAddKeyedSingleton<FileSystemStorage>(options, (sp, o) => new FileSystemStorage(options, sp.GetRequiredService<ICurrentTimeProvider>()));
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredKeyedService<FileSystemStorage>(options));

        return services;
    }
}
