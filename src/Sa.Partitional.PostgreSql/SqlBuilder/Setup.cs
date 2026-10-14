using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal static class Setup
{
    public static IServiceCollection AddSqlBuilder(this IServiceCollection services, string? key = null)
    {
        if (key is null)
        {
            services.TryAddSingleton<ISqlBuilderFactory, SqlBuilderFactory>();
            services.TryAddSingleton<ISqlBuilder>(sp => sp.GetRequiredService<ISqlBuilderFactory>().Create());
        }
        else
        {
            services.TryAddKeyedSingleton<ISqlBuilderFactory>(key, (sp, _)
                => new SqlBuilderFactory(sp.GetRequiredKeyedService<ISettingsBuilder>(key)));

            services.TryAddKeyedSingleton<ISqlBuilder>(key, (sp, _)
                => sp.GetRequiredKeyedService<ISqlBuilderFactory>(key).Create());
        }

        return services;
    }
}
