using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IO;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

internal sealed class PostgresFileStorageConfiguration : IPostgresFileStorageConfiguration
{
    private readonly PostgresFileStorageOptions _options = new();
    private readonly IPartConfiguration _partConfiguration;
    private Action<IServiceProvider, PostgresFileStorageOptions>? _configure;

    public PostgresFileStorageConfiguration(IServiceCollection services)
    {
        services.TryAddSingleton<RecyclableMemoryStreamManager>();

        _partConfiguration = services.AddSaPartitional((sp, builder) =>
        {
            var dataSource = sp.GetRequiredService<IPgDataSource>();
            _options.StorageOptions.SchemaName = dataSource.GetSearchPath();
            _configure?.Invoke(sp, _options);
            _options.StorageOptions.TableName = _options.StorageOptions.TableName.Trim('"');


            builder.AddSchema(_options.StorageOptions.SchemaName, schema =>
            {
                schema.AddTable(_options.StorageOptions.TableName,
                    "id TEXT NOT NULL",
                    "name TEXT NOT NULL",
                    "size INT NOT NULL",
                    "file_ext TEXT NOT NULL",
                    "tenant_id INT NOT NULL",
                    "scope_name TEXT NOT NULL",
                    "data BYTEA NOT NULL"
                )
                .PartByList("tenant_id", "scope_name")
                .PartByRange(_options.PartOptions.PgPartBy, "created_at");
            });
        })
        // Schedule for creating new partitions
        .AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.ForwardDays = _options.PartOptions.MigrationScheduleForwardDays;
        })
        // Schedule for removing old partitions
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(_options.CleanupOptions.ExpireDays);
        });

        services.AddSingleton<IFileStorage>(sp =>
        {
            var dataSource = sp.GetRequiredService<IPgDataSource>();
            var pm = sp.GetRequiredService<IPartitionManager>();
            var time = sp.GetService<TimeProvider>();
            var sm = sp.GetRequiredService<RecyclableMemoryStreamManager>();

            var storage = new PostgresFileStorage(
                dataSource: dataSource,
                partManager: pm,
                streamManager: sm,
                options: _options.StorageOptions,
                scopeName: _options.ScopeName,
                timeProvider: time);

            return storage;
        });
    }

    public IPostgresFileStorageConfiguration WithTableName(string tableName)
    {
        _options.StorageOptions.TableName = tableName;
        return this;
    }

    public IPostgresFileStorageConfiguration WithSchemaName(string schemaName)
    {
        _options.StorageOptions.SchemaName = schemaName;
        return this;
    }

    public IPostgresFileStorageConfiguration WithStorageType(string storageType)
    {
        _options.StorageOptions.StorageType = storageType;
        return this;
    }

    public IPostgresFileStorageConfiguration AsReadOnly()
    {
        _options.StorageOptions.IsReadOnly = true;
        return this;
    }

    public IPostgresFileStorageConfiguration ConfigureOptions(
        Action<IServiceProvider, PostgresFileStorageOptions> configure)
    {
        _configure = configure;
        return this;
    }

    public IPostgresFileStorageConfiguration AddDataSource(
        Action<IPgDataSourceSettingsBuilder>? configure = null)
    {
        _partConfiguration.AddDataSource(configure);
        return this;
    }
}
