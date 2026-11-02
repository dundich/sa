using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;


namespace Sa.Outbox.Partitional;

internal static class Setup
{
    public static IServiceCollection AddOutboxPartitional(this IServiceCollection services)
    {
        services.TryAddSingleton<ITenantProvider, TenantProvider>();
        // An empty shared fallback; a provider with its own WithTenants registers a keyed one.
        services.TryAddSingleton<TenantSettings>();
        return services;
    }

    public static IServiceCollection AddTenantSettings(
        this IServiceCollection services,
        string providerKey,
        Action<IServiceProvider, TenantSettings> configure)
    {
        // The provider's own settings: keyed-first resolution makes them win over the shared
        // fallback; a repeated call for the same key keeps the last one, as before.
        services
            .RemoveAllKeyed<TenantSettings>(providerKey)
            .AddKeyedSingleton<TenantSettings>(providerKey, (sp, _) =>
            {
                TenantSettings settings = new();
                configure.Invoke(sp, settings);
                return settings;
            });

        // The default provider doubles as the shared fallback for providers without their own
        // tenant settings, so a single-provider application (and code resolving TenantSettings
        // without a key) keeps its previous behaviour.
        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            services.RemoveAll<TenantSettings>();
            services.AddSingleton<TenantSettings>(
                sp => sp.GetRequiredKeyedService<TenantSettings>(providerKey));
        }

        return services;
    }
}
