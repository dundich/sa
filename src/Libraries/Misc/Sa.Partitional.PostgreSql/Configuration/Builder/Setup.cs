using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// The schema the tables default to when the configuration does not name one - the effective
    /// (first usable) entry of the data source's search path, or <c>null</c> (which the settings
    /// builder reads as <c>public</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IPgDataSource.GetSearchPath"/> is the raw <c>Search Path</c> value: a
    /// comma-separated list, possibly empty. Handing it to the settings builder unchanged turned
    /// <c>Search Path=tenant1, public</c> into one schema literally named <c>"tenant1, public"</c>;
    /// <see cref="SearchPathSchema"/> applies the same rule PostgreSQL does.
    /// </para>
    /// <para>
    /// Two "not configured yet" cases are tolerated on purpose, because the same registration is
    /// used without a database in unit tests and design-time tooling: no data source registered at
    /// all, and a source whose connection string is still empty. Every other failure - a malformed
    /// connection string, a factory that throws - must surface: swallowing it here silently moved
    /// the tables into <c>public</c> and the error only appeared later, at the first migration.
    /// </para>
    /// </remarks>
    private static string? GetDefaultSchema(IServiceProvider sp, string? key = null)
    {
        try
        {
            return PgDataSourceResolver.TryResolve(sp, key, out IPgDataSource? dataSource)
                ? SearchPathSchema.Resolve(dataSource!.GetSearchPath())
                : null;
        }
        catch (OptionsValidationException ex) when (ex.Failures.All(IsMissingConnectionString))
        {
            return null;
        }
    }

    private static bool IsMissingConnectionString(string failure)
        => failure.Contains("ConnectionString cannot be empty", StringComparison.Ordinal);
}
