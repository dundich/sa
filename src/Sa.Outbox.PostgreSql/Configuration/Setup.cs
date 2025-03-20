using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PostgreSql.Configuration;

internal static class Setup
{
    public static IServiceCollection AddPgOutboxSettings(this IServiceCollection services, Action<IPgOutboxConfiguration>? configure = null)
    {
        var cfg = new PgOutboxConfiguration(services);
        configure?.Invoke(cfg);
        
        cfg
            .WithPgOutboxSettings()
            .AddDataSource()
            ;
        
        return services;
    }
}
