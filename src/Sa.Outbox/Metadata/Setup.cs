using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.Metadata;

internal static class Setup
{
    public static IServiceCollection AddMessagesMetadata(
        this IServiceCollection services,
        Action<IServiceProvider, IOutboxMessageMetadataBuilder>? configure = null)
    {

        services.AddSingleton<MetadataConfiguration>(sp =>
        {
            var configuration = new MetadataConfiguration();
            configure?.Invoke(sp, configuration);
            return configuration;
        });


        services.TryAddSingleton<IOutboxMessageMetadataProvider>(sp =>
        {
            var configuration = new MetadataConfiguration();

            foreach (var config in sp.GetServices<MetadataConfiguration>())
            {
                configuration.MergeFrom(config);
            }

            return configuration;
        });

        return services;
    }
}
