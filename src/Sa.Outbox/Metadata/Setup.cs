using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.Metadata;

internal static class Setup
{
    public static IServiceCollection AddOutboxMessages(
        this IServiceCollection services,
        Action<IServiceProvider, IOutboxMessageMetadataBuilder> configure)
    {

        services.AddSingleton<MetadataConfiguration>(sp =>
        {
            var configuration = new MetadataConfiguration();
            configure(sp, configuration);
            return configuration;
        });


        services.TryAddSingleton<IOutboxMessageMetadataProvider>(sp =>
        {
            var configs = sp.GetServices<MetadataConfiguration>();
            if (configs.Count() != 1) return configs.First();

            var configuration = new MetadataConfiguration();

            foreach (var config in configs)
            {
                configuration.Assign(config);
            }

            return configuration;
        });

        return services;
    }
}
