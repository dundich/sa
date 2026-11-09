using Microsoft.Extensions.Configuration;
using Sa.Configuration.PostProcessing;
using Sa.Data.PostgreSql;

namespace Sa.Configuration.PostgreSql;

/// <summary>
/// Registers the PostgreSQL-backed <see cref="IConfiguration"/> source.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Adds a dynamic configuration source that reads <c>(key, value)</c> pairs from PostgreSQL
    /// through the application's already registered data source
    /// (<see cref="PgConfigurationConnection.FromServices(string?)"/>).
    /// </summary>
    /// <remarks>
    /// The application is expected to have registered <c>IPgDataSource</c> (for example with
    /// <c>AddSaPostgreSqlDataSource</c>) or <c>NpgsqlDataSource</c>; the source then shares that
    /// connection pool. Because configuration is assembled before the container, the data source is
    /// resolved during the first runtime initialization — register the runtime with
    /// <c>services.AddSaConfigurationRuntime(builder.Configuration)</c> and call
    /// <c>ISaConfigurationReloader.EnsureInitializedAsync</c> (or enable the hosted hook).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The query is blank or the options are inconsistent.</exception>
    public static IConfigurationBuilder AddSaPostgreSqlConfiguration(
        this IConfigurationBuilder builder, PostgreSqlConfigurationOptions options)
        => AddSaPostgreSqlConfiguration(builder, PgConfigurationConnection.FromServices(), options);

    /// <summary>
    /// Adds a dynamic configuration source with an explicit connection strategy.
    /// </summary>
    /// <remarks>
    /// The source is placed after the sources registered so far and the secret/alias pass is moved
    /// after it, so the database values override the static configuration and the aliases are
    /// derived from the freshest values.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The connection or the options are invalid.</exception>
    public static IConfigurationBuilder AddSaPostgreSqlConfiguration(
        this IConfigurationBuilder builder,
        PgConfigurationConnection connection,
        PostgreSqlConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        builder.Add(new DatabaseConfigurationSource(options, connection));

        // Deterministic ordering without a manual Finalize: a database source added after
        // AddSaConfiguration would otherwise beat the alias pass for the keys it holds.
        if (SaConfigurationState.Find(builder) is { } state)
            PostProcessingPipeline.MoveToEnd(builder, state);

        return builder;
    }

    /// <summary>
    /// Adds a dynamic configuration source that owns a private connection pool.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The connection string is blank or invalid, or the options are invalid.</exception>
    public static IConfigurationBuilder AddSaPostgreSqlConfiguration(
        this IConfigurationBuilder builder,
        string connectionString,
        PostgreSqlConfigurationOptions options)
        => AddSaPostgreSqlConfiguration(builder, PgConfigurationConnection.ConnectionString(connectionString), options);

    /// <summary>
    /// Adds a dynamic configuration source that reuses a data source owned by the caller — typically
    /// the application's own pool, so the configuration source opens no second one.
    /// </summary>
    /// <remarks>
    /// The caller keeps ownership: the provider never disposes <paramref name="dataSource"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static IConfigurationBuilder AddSaPostgreSqlConfiguration(
        this IConfigurationBuilder builder,
        IPgDataSource dataSource,
        PostgreSqlConfigurationOptions options)
        => AddSaPostgreSqlConfiguration(builder, PgConfigurationConnection.DataSource(dataSource), options);
}
