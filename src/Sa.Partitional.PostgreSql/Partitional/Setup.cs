using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sa.Data.PostgreSql;

namespace Sa.Partitional.PostgreSql.Repositories;

internal static class Setup
{
    public static IServiceCollection AddPartRepository(this IServiceCollection services, string? key = null)
    {
        if (key is null)
        {
            services.TryAddSingleton<IPartRepository, PartRepository>();
        }
        else
        {
            services.TryAddKeyedSingleton<IPartRepository>(key, (sp, _) => new PartRepository(
                PgDataSourceResolver.Resolve(sp, key),
                sp.GetRequiredKeyedService<ISqlBuilder>(key),
                sp.GetRequiredService<ILogger<PartRepository>>()));
        }

        return services;
    }
}
