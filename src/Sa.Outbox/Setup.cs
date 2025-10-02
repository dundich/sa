using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Configuration;

namespace Sa.Outbox;

public static class Setup
{
    public static IServiceCollection AddOutbox(this IServiceCollection services, Action<IOutboxBuilder>? build = null)
    {
        OutboxBuilder builder = new(services);
        build?.Invoke(builder);
        return services;
    }
}
