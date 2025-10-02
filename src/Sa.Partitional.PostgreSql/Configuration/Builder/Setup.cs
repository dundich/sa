using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Concurrent;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal static class Setup
{
    private static readonly ConcurrentDictionary<IServiceCollection, HashSet<Action<IServiceProvider, ISettingsBuilder>>> s_invokers = [];

    public static IServiceCollection AddSettigs(this IServiceCollection services, Action<IServiceProvider, ISettingsBuilder> build)
    {
        if (s_invokers.TryGetValue(services, out var invokers))
        {
            invokers.Add(build);
        }
        else
        {
            s_invokers[services] = [build];
        }

        services.TryAddSingleton<ISettingsBuilder>(sp =>
        {
            var builder = new SettingsBuilder();
            if (s_invokers.TryGetValue(services, out var invokers))
            {
                foreach (Action<IServiceProvider, ISettingsBuilder> build in invokers)
                {
                    build.Invoke(sp, builder);
                }

                s_invokers.Remove(services, out _);
            }
            return builder;
        });

        return services;
    }
}
