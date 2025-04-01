using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.PostgresFileStorage;


public static class Setup
{
    public static IServiceCollection AddPostgresHybridFileStorage(this IServiceCollection services, PostgresStorageOptions option)
    {
        services.AddPartitional((sp, builder) =>
        {
            builder.AddSchema(option.SchemaName, schema =>
            {
                schema.AddTable(option.TableName,
                    "id TEXT NOT NULL",
                    "name TEXT NOT NULL",
                    "size INT NOT NULL",
                    "file_ext TEXT NOT NULL",
                    "tenant_id INT NOT NULL",
                    "data BYTEA NOT NULL"
                )
                .PartByList("tenant_id")
                .PartByRange(PgPartBy.Day, "created_at")
                ;
            });
        })
        // Schedule for creating new partitions
        .AddPartMigrationSchedule((sp, opts) =>
        {
            opts.AsJob = true;
            opts.ForwardDays = option.MigrationScheduleForwardDays;
        })
        // Schedule for removing old partitions
        .AddPartCleanupSchedule((sp, opts) =>
        {
            opts.AsJob = true;
            opts.DropPartsAfterRetention = TimeSpan.FromDays(option.ExpireDays);
        })
        ;

        services.TryAddKeyedSingleton<PostgresStorageOptions>(option);
        services.TryAddKeyedSingleton<PostgresFileStorage>(option);

        services.AddSingleton<IFileStorage>(sp =>
        {
            var storage = sp.GetRequiredKeyedService<PostgresFileStorage>(option);
            return storage.WithOptions(option);
        });

        return services;
    }
}
