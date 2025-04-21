using Microsoft.Extensions.DependencyInjection;
using Microsoft.IO;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.PostgresFileStorage;

internal class PostgresFileStorageConfiguration : IPostgresFileStorageConfiguration
{
    private readonly PostgresFileStorageOptions _options = new();
    private readonly IPartConfiguration _partConfiguration;
    private Action<IServiceProvider, PostgresFileStorageOptions>? _configure;

    public PostgresFileStorageConfiguration(IServiceCollection services)
    {

        _partConfiguration = services.AddPartitional((sp, builder) =>
        {
            builder.AddSchema(_options.StorageOptions.SchemaName, schema =>
            {
                schema.AddTable(_options.StorageOptions.TableName,
                    "id TEXT NOT NULL",
                    "name TEXT NOT NULL",
                    "size INT NOT NULL",
                    "file_ext TEXT NOT NULL",
                    "tenant_id INT NOT NULL",
                    "data BYTEA NOT NULL"
                )
                .PartByList("tenant_id")
                .PartByRange(_options.PartOptions.PgPartBy, "created_at");
            });
        })
        // Schedule for creating new partitions
        .AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsJob = true;
            opts.ForwardDays = _options.PartOptions.MigrationScheduleForwardDays;
        })
        // Schedule for removing old partitions
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(_options.CleanupOptions.ExpireDays);
        });

        services.AddSingleton<IFileStorage>(sp =>
        {
            _configure?.Invoke(sp, _options);
            _configure = null;

            var pm = sp.GetRequiredService<IPartitionManager>();
            var dataSource = sp.GetRequiredService<IPgDataSource>();
            var time = sp.GetRequiredService<ICurrentTimeProvider>();
            var sm = sp.GetRequiredService<RecyclableMemoryStreamManager>();

            var storage = new PostgresFileStorage(dataSource, pm, time, sm, _options.StorageOptions);
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

    public IPostgresFileStorageConfiguration ConfigureOptions(Action<PostgresFileStorageOptions> configure)
    {
        configure?.Invoke(_options);
        return this;
    }

    public IPostgresFileStorageConfiguration ConfigureOptions(Action<IServiceProvider, PostgresFileStorageOptions> configure)
    {
        _configure = configure;
        return this;
    }

    public IPostgresFileStorageConfiguration AddDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null)
    {
        _partConfiguration.AddDataSource(configure);
        return this;
    }
}
