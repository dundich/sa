using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.PostgreSql.IdGen;

internal static class Setup
{
    public static IServiceCollection AddOutboxIdGen(this IServiceCollection services)
    {
        services
            .RemoveAll<IOutboxIdGenerator>()
            .AddSingleton<IOutboxIdGenerator, OutboxIdGenerator>();

        return services;
    }
}
