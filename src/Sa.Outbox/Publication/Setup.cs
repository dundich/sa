using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Metadata;

namespace Sa.Outbox.Publication;

internal static class Setup
{
    public static IServiceCollection AddMessagePublisher(
        this IServiceCollection services,
        Action<IServiceProvider, OutboxPublishSettings>? configure = null)
    {
        services.AddMessagesMetadata();

        if (configure != null)
        {
            services.AddSingleton(configure);
        }

        services.TryAddSingleton<OutboxPublishSettings>(sp =>
        {
            OutboxPublishSettings settings = new();

            foreach (var build in sp.GetServices<Action<IServiceProvider, OutboxPublishSettings>>())
            {
                build(sp, settings);
            }

            return settings;
        });

        services.TryAddSingleton<IOutboxMessagePublisher, OutboxMessagePublisher>();
        return services;
    }
}
