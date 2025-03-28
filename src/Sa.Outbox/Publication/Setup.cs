using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.Publication;

internal static class Setup
{
    public static IServiceCollection AddMessagePublisher(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxMessagePublisher, OutboxMessagePublisher>();
        return services;
    }
}
