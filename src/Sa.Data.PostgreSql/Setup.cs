using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Sa.Data.PostgreSql.Configuration;

namespace Sa.Data.PostgreSql;

public static class Setup
{
    public static IServiceCollection AddSaPostgreSqlDataSource(
        this IServiceCollection services,
        Action<IPgDataSourceSettingsBuilder>? configure = null)
    {
        PgDataSourceSettingsBuilder builder = new(services);
        configure?.Invoke(builder);
        services.TryAddSingleton<IPgDataSource>(sp =>
        {
            PgDataSourceSettings? settings = sp.GetService<PgDataSourceSettings>();

            if (settings is null)
            {
                var connection = sp.GetService<NpgsqlDataSource>()?.ConnectionString
                   ?? throw new InvalidOperationException("Empty connection string");
                settings = new(connection);
            }

            settings.Validate();

            return new PgDataSource(settings);
        });
        services.TryAddSingleton<IPgDistributedLock, PgDistributedLock>();
        return services;
    }
}
