using Microsoft.Extensions.Configuration;
using Sa.Data.PostgreSql;

namespace Sa.Configuration.PostgreSql;

/// <summary>
/// Configuration source backed by a PostgreSQL query. Each build produces one
/// <see cref="DatabaseConfigurationProvider"/> holding its own data source and connection pool.
/// </summary>
public sealed class DatabaseConfigurationSource(PostgreSqlConfigurationOptions options, IPgDataSource? dataSource = null)
    : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(options);

        return dataSource is null
            ? new DatabaseConfigurationProvider(options)
            : new DatabaseConfigurationProvider(options, dataSource, ownsDataSource: false);
    }
}
