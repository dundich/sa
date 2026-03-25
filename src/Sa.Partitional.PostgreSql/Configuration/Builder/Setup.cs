using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal static class Setup
{
    public static IServiceCollection AddSettings(
        this IServiceCollection services,
        Action<IServiceProvider, ISettingsBuilder> build)
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

        return services;
    }

    private static string? GetDefaultSchema(IServiceProvider sp)
    {
        try
        {
            return sp.GetService<IPgDataSource>()?.GetSearchPath();
        }
        catch
        {
            return null;
        }
    }
}
