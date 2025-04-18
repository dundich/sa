using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PostgreSql.Configuration;

internal static class Setup
{
    public static IServiceCollection AddPgOutboxSettings(this IServiceCollection services, Action<IPgOutboxConfiguration>? configure = null)
    {
        var cfg = new PgOutboxConfiguration(services);
        configure?.Invoke(cfg);
        
        cfg
            .ConfigureOutboxSettings()
            .ConfigureDataSource()
            ;
        
        return services;
    }
}
