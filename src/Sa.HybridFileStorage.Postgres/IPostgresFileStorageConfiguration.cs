using Sa.Data.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Defines a fluent configuration pipeline for the PostgreSQL file storage provider.
/// </summary>
public interface IPostgresFileStorageConfiguration
{
    /// <summary>
    /// Configures a custom PostgreSQL data source for connecting to the database.
    /// </summary>
    /// <param name="configure">An optional action to configure the PostgreSQL data source settings builder.</param>
    /// <returns>The same <see cref="IPostgresFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IPostgresFileStorageConfiguration AddDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);

    /// <summary>
    /// Configures the <see cref="PostgresFileStorageOptions"/> after the storage provider is registered.
    /// </summary>
    /// <param name="configure">An action that receives an <see cref="IServiceProvider"/> and <see cref="PostgresFileStorageOptions"/> for customization.</param>
    /// <returns>The same <see cref="IPostgresFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IPostgresFileStorageConfiguration ConfigureOptions(Action<IServiceProvider, PostgresFileStorageOptions> configure);

    /// <summary>
    /// Sets the storage type identifier used in file IDs. Defaults to <c>"pg"</c>.
    /// </summary>
    /// <param name="storageType">The storage type identifier.</param>
    /// <returns>The same <see cref="IPostgresFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IPostgresFileStorageConfiguration WithStorageType(string storageType);

    /// <summary>
    /// Sets the database schema name where the files table resides. Defaults to <c>"public"</c>.
    /// </summary>
    /// <param name="schemaName">The database schema name.</param>
    /// <returns>The same <see cref="IPostgresFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IPostgresFileStorageConfiguration WithSchemaName(string schemaName);

    /// <summary>
    /// Sets the database table name for storing file metadata. Defaults to <c>"files"</c>.
    /// </summary>
    /// <param name="tableName">The database table name.</param>
    /// <returns>The same <see cref="IPostgresFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IPostgresFileStorageConfiguration WithTableName(string tableName);

    /// <summary>
    /// Marks the PostgreSQL storage as read-only, preventing write operations.
    /// </summary>
    /// <returns>The same <see cref="IPostgresFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IPostgresFileStorageConfiguration AsReadOnly();
}
