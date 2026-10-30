namespace Sa.Configuration.PostgreSql;

using Microsoft.Extensions.Configuration;
using Sa.Data.PostgreSql;


/// <summary>
/// Registers the PostgreSQL-backed <see cref="IConfiguration"/> source.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Adds a configuration source that reads <c>(key, value)</c> pairs from PostgreSQL.
    /// </summary>
    /// <remarks>
    /// The source owns a private connection pool, created once and reused across every reload.
    /// Prefer <see cref="AddSaPostgreSqlConfiguration(IConfigurationBuilder, PostgreSqlConfigurationOptions, IPgDataSource)"/>
    /// to share the application's pool instead.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The connection string or query is blank.</exception>
    public static IConfigurationBuilder AddSaPostgreSqlConfiguration(
        this IConfigurationBuilder builder, PostgreSqlConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        return builder.Add(new DatabaseConfigurationSource(options));
    }

    /// <summary>
    /// Adds a configuration source that reads <c>(key, value)</c> pairs from PostgreSQL through
    /// an existing data source — typically the DI-registered <see cref="IPgDataSource"/>, so the
    /// configuration source reuses the application's connection pool rather than opening its own.
    /// The caller keeps ownership: the provider never disposes <paramref name="dataSource"/>.
    /// </summary>
    /// <remarks>
    /// Because the data source carries the connection, <see cref="PostgreSqlConfigurationOptions.ConnectionString"/>
    /// is ignored and may be left empty.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The query is blank.</exception>
    public static IConfigurationBuilder AddSaPostgreSqlConfiguration(
        this IConfigurationBuilder builder, PostgreSqlConfigurationOptions options, IPgDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataSource);

        options.Validate(requireConnectionString: false);

        return builder.Add(new DatabaseConfigurationSource(options, dataSource));
    }
}
