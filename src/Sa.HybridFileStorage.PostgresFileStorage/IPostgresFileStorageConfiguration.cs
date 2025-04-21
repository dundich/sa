using Sa.Data.PostgreSql;

namespace Sa.HybridFileStorage.PostgresFileStorage;

public interface IPostgresFileStorageConfiguration
{
    IPostgresFileStorageConfiguration AddDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);
    IPostgresFileStorageConfiguration ConfigureOptions(Action<IServiceProvider, PostgresFileStorageOptions> configure);
    IPostgresFileStorageConfiguration ConfigureOptions(Action<PostgresFileStorageOptions> configure);
    IPostgresFileStorageConfiguration WithStorageType(string storageType);
    IPostgresFileStorageConfiguration WithSchemaName(string schemaName);
    IPostgresFileStorageConfiguration WithTableName(string tableName);
    IPostgresFileStorageConfiguration AsReadOnly();
}
