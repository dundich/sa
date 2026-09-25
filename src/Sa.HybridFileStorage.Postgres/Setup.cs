using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IO;
using Sa.Data.PostgreSql;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Provides extension methods for registering the PostgreSQL file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the PostgreSQL file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configureOptions">An optional action to configure the PostgreSQL storage options.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        Action<PostgresFileStorageOptions>? configureOptions = null)
    {
        RegisterCore(services, configureOptions);
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider with the specified service collection.
    /// Returns an <see cref="IPartConfiguration"/> for chaining DataSource configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configureOptions">An optional action to configure the PostgreSQL storage options.</param>
    /// <returns>An <see cref="IPartConfiguration"/> for chaining DataSource configuration.</returns>
    public static IPartConfiguration AddSaPostgreSqlFileStorageChained(
        this IServiceCollection services,
        Action<PostgresFileStorageOptions>? configureOptions = null)
    {
        return RegisterCore(services, configureOptions);
    }

    /// <summary>
    /// Performs the shared registration logic for both <see cref="AddSaPostgreSqlFileStorage"/> and
    /// <see cref="AddSaPostgreSqlFileStorageChained"/>, returning the partition configuration so callers
    /// can optionally chain DataSource configuration.
    /// </summary>
    private static IPartConfiguration RegisterCore(
        IServiceCollection services,
        Action<PostgresFileStorageOptions>? configureOptions)
    {
        var options = new PostgresFileStorageOptions();
        configureOptions?.Invoke(options);

        // Trim quotes from table name if accidentally included
        options.TableName = options.TableName.Trim('"');

        // 1. RecyclableMemoryStreamManager (singleton, shared across all instances)
        services.TryAddSingleton<RecyclableMemoryStreamManager>();

        // 2. Partitioning setup + DataSource configuration
        IPartConfiguration partConfig = services.AddSaPartitional((sp, builder) =>
        {
            var dataSource = sp.GetRequiredService<IPgDataSource>();
            // Auto-detect schema from connection search_path
            options.SchemaName = dataSource.GetSearchPath();

            builder.AddSchema(options.SchemaName, schema =>
            {
                schema.AddTable(options.TableName,
                    "id TEXT NOT NULL",
                    "name TEXT NOT NULL",
                    "size INT NOT NULL",
                    "file_ext TEXT NOT NULL",
                    "tenant_id INT NOT NULL",
                    "basket TEXT NOT NULL",
                    "data BYTEA NOT NULL"
                )
                .PartByList("tenant_id", "basket")
                .PartByRange(options.PgPartBy, "created_at");
            });
        });

        // 3. Schedule for creating new partitions
        partConfig.AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.ForwardDays = options.MigrationScheduleForwardDays;
        })
        // Schedule for removing old partitions
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsBackgroundJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(options.ExpireDays);
        });

        // 4. Register IFileStorage singleton
        services.AddSingleton<IFileStorage>(sp => new PostgresFileStorage(
            dataSource: sp.GetRequiredService<IPgDataSource>(),
            partManager: sp.GetRequiredService<IPartitionManager>(),
            streamManager: sp.GetRequiredService<RecyclableMemoryStreamManager>(),
            options: options,
            timeProvider: sp.GetService<TimeProvider>() ?? TimeProvider.System));

        return partConfig;
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Configuration options for the PostgreSQL storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        PostgresFileStorageOptions options)
    {
        return services.AddSaPostgreSqlFileStorage(opts =>
        {
            opts.SchemaName = options.SchemaName;
            opts.TableName = options.TableName;
            opts.StorageType = options.StorageType;
            opts.IsReadOnly = options.IsReadOnly;
            opts.Basket = options.Basket;
            opts.ExpireDays = options.ExpireDays;
            opts.MigrationScheduleForwardDays = options.MigrationScheduleForwardDays;
            opts.PgPartBy = options.PgPartBy;
        });
    }
}
