using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.PostgreSql.IdGen;

internal static class Setup
{
    public static IServiceCollection AddIdGen(this IServiceCollection services)
    {
        services
            .RemoveAll<IIdGenerator>()
            .AddSingleton<IIdGenerator, IdGenerator>();

        return services;
    }
}
