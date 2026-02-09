namespace Sa.Configuration.PostgreSql;

using Microsoft.Extensions.Configuration;


public static class Setup
{
    public static IConfigurationBuilder AddPostgreSqlConfiguration(
        this IConfigurationBuilder builder, PostgreSqlConfigurationOptions options)
    {
        return builder.Add(new DatabaseConfigurationSource(options
            ?? throw new ArgumentNullException(nameof(options))));
    }
}
