using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Data.PostgreSql.Configuration;

internal class PgDataSourceSettingsBuilder(IServiceCollection services) : IPgDataSourceSettingsBuilder
{
    public void WithConnectionString(string connectionString)
    {
        services.TryAddSingleton<PgDataSourceSettings>(new PgDataSourceSettings(connectionString));
    }

    public void WithConnectionString(Func<IServiceProvider, string> implementationFactory)
    {
        services.TryAddSingleton<PgDataSourceSettings>(sp => new PgDataSourceSettings(implementationFactory(sp)));
    }

    public void WithSettings(Func<IServiceProvider, PgDataSourceSettings> implementationFactory)
    {
        services.TryAddSingleton<PgDataSourceSettings>(implementationFactory);
    }

    public void WithSettings(PgDataSourceSettings settings)
    {
        services.TryAddSingleton<PgDataSourceSettings>(settings);
    }
}
