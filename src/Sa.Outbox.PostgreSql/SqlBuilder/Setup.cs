using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.ObjectPool;
using Sa.Outbox.PostgreSql.Configuration;
using System.Text;

namespace Sa.Outbox.PostgreSql.SqlBuilder;

public static class Setup
{
    internal static IServiceCollection AddOutboxSqlBuilder(this IServiceCollection services, Action<IPgOutboxConfiguration>? configure = null)
    {
        services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();

        services.AddSingleton<ObjectPool<StringBuilder>>(serviceProvider =>
        {
            var provider = serviceProvider.GetRequiredService<ObjectPoolProvider>();
            var policy = new StringBuilderPooledObjectPolicy()
            {
                InitialCapacity = 1024,
            };
            return provider.Create(policy);
        });

        services.TryAddSingleton<SqlOutboxBuilder>();

        services.AddPgOutboxSettings(configure);

        return services;
    }
}
