using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Partitional.PostgreSql.Configuration;

namespace Sa.Partitional.PostgreSql.Cache;

internal static class Setup
{
    public static IServiceCollection AddPartCache(
        this IServiceCollection services,
        string? key = null,
        Action<IPartSettingsBuilder<PartCacheSettings>>? configure = null)
    {
        string name = PartOptions.Name(key);

        // Named options instance per module (name = module key). The order is fixed by PartOptions:
        // section → code → Options(...) actions → validation, collected across every configuring
        // call of the module. The binder runs at this concretely typed call site, so the
        // configuration source generator sees it (Native AOT).
        PartOptions.Configure(services, name, BindFromConfiguration, configure);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PartCacheSettings>, PartCacheSettingsValidator>());

        // The concrete settings the services and tests consume, materialised from the named options
        // instance. TryAdd: a repeated AddSaPartitional(...) call must not replace the instance an
        // already constructed PartCache captured in its constructor.
        if (key is null)
        {
            services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<PartCacheSettings>>().Value);
            services.TryAddSingleton<IPartCache, PartCache>();
        }
        else
        {
            services.TryAddKeyedSingleton(key, (sp, _) =>
                sp.GetRequiredService<IOptionsMonitor<PartCacheSettings>>().Get(key));

            services.TryAddKeyedSingleton<IPartCache>(key, (sp, _) => new PartCache(
                sp.GetRequiredKeyedService<IPartRepository>(key),
                sp.GetRequiredKeyedService<ISqlBuilder>(key),
                sp.GetRequiredKeyedService<PartCacheSettings>(key),
                sp.GetService<TimeProvider>()));
        }

        return services;
    }

    /// <summary>
    /// Concrete binder for <see cref="PartCacheSettings"/>, called by the module's configurator. The
    /// call site names the settings type, which is what lets the configuration source generator bake
    /// the binding logic under Native AOT.
    /// </summary>
    private static void BindFromConfiguration(
        PartCacheSettings options, IConfiguration configuration, string sectionPath)
        => configuration.GetSection(sectionPath).Bind(options);
}
