using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Partitional.PostgreSql.Repositories;

public static class Setup
{
    public static IServiceCollection AddPartRepository(this IServiceCollection services)
    {
        services.TryAddSingleton<IPartRepository, PartRepository>();
        return services;
    }
}
