using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal static class Setup
{
    public static IServiceCollection AddSettings(
        this IServiceCollection services,
        string? key,
        Action<IServiceProvider, ISettingsBuilder> build)
    {
        if (key is null)
        {
            services.AddSingleton(build);

            services.TryAddSingleton<ISettingsBuilder>(sp =>
            {
                SettingsBuilder builder = new(GetDefaultSchema(sp));

                var configurators = sp.GetServices<Action<IServiceProvider, ISettingsBuilder>>();

                foreach (var configure in configurators)
                {
                    configure(sp, builder);
                }

                return builder;
            });
        }
        else
        {
            // The delegates accumulate per key: a repeated AddSaPartitional(key, ...) call merges
            // its tables into the module's builder, exactly like the keyless overload does for the
            // default module. A different key never sees these delegates.
            services.AddKeyedSingleton(key, build);

            services.TryAddKeyedSingleton<ISettingsBuilder>(key, (sp, _) =>
            {
                SettingsBuilder builder = new(GetDefaultSchema(sp, key));

                var configurators = sp.GetKeyedServices<Action<IServiceProvider, ISettingsBuilder>>(key);

                foreach (var configure in configurators)
                {
                    configure(sp, builder);
                }

                return builder;
            });
        }

        return services;
    }

    private static string? GetDefaultSchema(IServiceProvider sp, string? key = null)
    {
        try
        {
            return PgDataSourceResolver.Resolve(sp, key).GetSearchPath();
        }
        catch
        {
            return null;
        }
    }
}
