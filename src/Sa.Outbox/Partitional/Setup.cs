using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;


namespace Sa.Outbox.Partitional;

internal static class Setup
{
    public static IServiceCollection AddOutboxPartitional(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxPartitionalSupport, OutboxPartitionalSupport>();
        return services;
    }

    public static IServiceCollection AddTenantProvider(this IServiceCollection services, Action<IServiceProvider, TenantSettings> configure)
    {
        // support - messaging to each tenant
        services.TryAddSingleton<ITenantProvider, TenantProvider>();

        services
            .RemoveAll<TenantSettings>()
            .AddSingleton<TenantSettings>(sp =>
            {
                TenantSettings settings = new();
                configure.Invoke(sp, settings);
                return settings;
            });

        return services;
    }
}
