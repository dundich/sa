using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox;

public static class Setup
{
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        Action<IOutboxBuilder>? build = null)
    {
        OutboxBuilder builder = OutboxBuilder.Create(services);
        build?.Invoke(builder);
        return services;
    }
}
