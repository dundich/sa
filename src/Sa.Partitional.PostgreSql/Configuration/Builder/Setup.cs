using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal static class Setup
{
    public static IServiceCollection AddSettigs(this IServiceCollection services, Action<IServiceProvider, ISettingsBuilder> build)
    {
        services.AddSingleton(build);

        services.TryAddSingleton<ISettingsBuilder>(sp =>
        {
            string? searchPath = GetSearchPath(sp);
            var builder = new SettingsBuilder(searchPath);

            var configurators = sp.GetServices<Action<IServiceProvider, ISettingsBuilder>>();

            foreach (var configure in configurators)
            {
                configure(sp, builder);
            }

            return builder;
        });

        return services;
    }

    private static string? GetSearchPath(IServiceProvider sp)
    {
        var settings = sp.GetService<PgDataSourceSettings>();
        var connectionString = settings?.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).SearchPath;
        }
        catch
        {
            return null;
        }
    }
}
