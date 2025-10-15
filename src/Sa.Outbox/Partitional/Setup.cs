using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;


namespace Sa.Outbox.Partitional;

internal static class Setup
{
    public static IServiceCollection AddPartitioningSupport(this IServiceCollection services, Action<IServiceProvider, PartitionalSettings> configure)
    {
        services.TryAddSingleton<IOutboxPartitionalSupport, OutboxPartitionalSupport>();

        services.TryAddSingleton<PartitionalSettings>(sp =>
        {
            PartitionalSettings settings = new();
            configure.Invoke(sp, settings);
            return settings;
        });

        services.TryAddSingleton<IPartitionalSupportCache, PartitionalSupportCache>();

        return services;
    }
}
