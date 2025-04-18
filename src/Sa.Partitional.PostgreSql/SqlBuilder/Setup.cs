using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal static class Setup
{
    public static IServiceCollection AddSqlBuilder(this IServiceCollection services)
    {
        services.TryAddSingleton<ISqlBuilderFactory, SqlBuilderFactory>();
        services.TryAddSingleton<ISqlBuilder>(sp => sp.GetRequiredService<ISqlBuilderFactory>().Create());

        return services;
    }
}
