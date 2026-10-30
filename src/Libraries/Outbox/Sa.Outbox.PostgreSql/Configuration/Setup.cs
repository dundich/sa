using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PostgreSql.Configuration;

internal static class Setup
{
    public static IServiceCollection AddPgOutboxSettings(
        this IServiceCollection services,
        Action<IPgOutboxConfiguration>? configure = null)
    {
        var configuration = new PgOutboxConfiguration(services)
            .WithDefaultSerializer()
            .WithOutboxSettings()
            .WithDataSource()
        ;

        configure?.Invoke(configuration);



        return services;
    }
}
