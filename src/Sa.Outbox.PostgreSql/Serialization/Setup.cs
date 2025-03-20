using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.PostgreSql.Serialization;

public static class Setup
{
    public static IServiceCollection AddOutboxMessageSerializer(this IServiceCollection services, PgOutboxSerializeSettings? settings = null)
    {
        services.TryAddSingleton<IOutboxMessageSerializer>(sp 
            => new OutboxMessageSerializer().WithOptions((settings ?? sp.GetRequiredService<PgOutboxSerializeSettings>()).JsonSerializerOptions));
        return services;
    }
}
