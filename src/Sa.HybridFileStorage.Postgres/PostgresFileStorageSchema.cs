using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Resolves the schema the file table lives in, once, and shares it between the partitioning
/// DDL and the storage itself.
/// </summary>
/// <remarks>
/// The two consumers used to disagree by accident: the DDL registration ran inside the lazy
/// <c>ISettingsBuilder</c> factory and *assigned* <c>SchemaName</c> onto the shared options
/// instance, while <see cref="PostgresFileStorage"/> read that same field from a field
/// initializer at construction. That only worked because <c>IPartitionManager</c> happens to
/// be resolved before the storage is constructed (arguments are evaluated left to right) and
/// because the partition graph reaches <c>ISettingsBuilder</c>. Resolving the value through a
/// dedicated service removes both the shared mutation and the ordering dependency.
/// </remarks>
internal sealed class PostgresFileStorageSchema
{
    /// <summary>
    /// The schema assumed when neither an explicit name nor a usable search path is available.
    /// </summary>
    public const string FallbackSchema = "public";

    private PostgresFileStorageSchema(string schema)
    {
        Value = schema;
    }

    /// <summary>
    /// Gets the resolved schema name: a bare, validated SQL identifier.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Registers the schema resolver for a keyed registration, deriving the value from the function's
    /// result when it returns a name and otherwise from the first entry of the module data source's
    /// search path.
    /// </summary>
    /// <param name="services">The service collection to add the resolver to.</param>
    /// <param name="key">The registration key the resolver is registered under.</param>
    /// <param name="dataSourceKey">
    /// The key the module's data source is registered under (the partitional module key), or the
    /// default (unkeyed) data source when no named one exists.
    /// </param>
    /// <param name="explicitSchema">
    /// Reads the schema configured by the user from the live options instance, or <c>null</c> to
    /// auto-detect. A function rather than a value: under the options pipeline the schema is only
    /// known once the named instance materialises (and reading it is what runs validation).
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance.</returns>
    public static IServiceCollection Register(
        IServiceCollection services,
        string key,
        string dataSourceKey,
        Func<IServiceProvider, string?> explicitSchema)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSourceKey);
        ArgumentNullException.ThrowIfNull(explicitSchema);

        services.TryAddKeyedSingleton(key, (sp, _) =>
            new PostgresFileStorageSchema(Resolve(sp, dataSourceKey, explicitSchema(sp))));

        return services;
    }

    private static string Resolve(IServiceProvider sp, string dataSourceKey, string? explicitSchema)
    {
        if (explicitSchema is not null)
        {
            return explicitSchema;
        }

        // The first schema of a comma-separated search path is the effective one for table
        // resolution. GetSearchPath() parses the connection string rather than opening a
        // connection, and already falls back to "public" when it cannot parse one.
        string? searchPath = TryGetSearchPath(sp, dataSourceKey);

        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return FallbackSchema;
        }

        string first = searchPath.Split(',')[0].Trim();
        return string.IsNullOrWhiteSpace(first) ? FallbackSchema : first;
    }

    private static string? TryGetSearchPath(IServiceProvider sp, string dataSourceKey)
    {
        try
        {
            // Same rule as the partitional module's data-source resolution: the named source pinned
            // under the module key when one exists, the shared default source next, and an
            // application-registered NpgsqlDataSource last (see PgDataSourceResolver). The last step
            // is what lets a container with only AddNpgsqlDataSource(...) report its Search Path
            // instead of silently falling back to "public".
            return PgDataSourceResolver.Resolve(sp, dataSourceKey).GetSearchPath();
        }
        catch
        {
            // A data source that cannot report its search path (misconfigured connection string,
            // provider not registered yet) must not take the whole registration down — the
            // fallback keeps the provider usable against the default schema.
            return null;
        }
    }
}
