using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.PostgreSql;

public class DatabaseConfigurationSource(PostgreSqlConfigurationOptions options) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new DatabaseConfigurationProvider(options);
}
