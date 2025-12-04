using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Partitional;

internal static class Setup
{
    public static IServiceCollection AddOutboxPartitional(this IServiceCollection services)
    {
        services.TryAddSingleton<IPartTableMigrationSupport, OutboxMigrationSupport>();

        services.AddPartitional((sp, builder) =>
        {
            SqlOutboxTemplate sql = sp.GetRequiredService<SqlOutboxTemplate>();
            IPartTableMigrationSupport? migrationSupport = sp.GetService<IPartTableMigrationSupport>();

            builder.AddSchema(sql.DatabaseSchemaName, schema =>
            {
                ITableBuilder outboxTableBuilder = schema
                    .AddTable(sql.DatabaseOutboxTableName, SqlOutboxTemplate.OutboxFields)
                    .PartByList("outbox_tenant", "outbox_part")
                    .TimestampAs("outbox_created_at")
                    .AddPostSql(() => sql.SqlCreateTypeTable)
                ;

                ITableBuilder deliveryTableBuilder = schema
                    .AddTable(sql.DatabaseDeliveryTableName, SqlOutboxTemplate.DeliveryFields)
                    .PartByList("delivery_tenant", "delivery_part")
                    .TimestampAs("delivery_created_at")
                ;

                ITableBuilder errorTableBuilder = schema
                    .AddTable(sql.DatabaseErrorTableName, SqlOutboxTemplate.ErrorFields)
                    .TimestampAs("error_created_at")
                ;

                if (migrationSupport != null)
                {
                    outboxTableBuilder.AddMigration(migrationSupport);
                    deliveryTableBuilder.AddMigration(migrationSupport);
                }

                errorTableBuilder.AddMigration();


                //var settings = sp.GetRequiredService<IScheduleSettings>()
                //    .GetJobSettings()
                //    .Select(c => c.Properties.Tag as OutboxDeliverySettings)
                //    .Where(c => c != null)
                //    .


            })
            ;
        })
        .AddPartMigrationSchedule((sp, opts) =>
        {
            PgOutboxMigrationSettings settings = sp.GetRequiredService<PgOutboxMigrationSettings>();
            opts.AsJob = settings.AsJob;
            opts.ExecutionInterval = settings.ExecutionInterval;
            opts.ForwardDays = settings.ForwardDays;
        })
        .AddPartCleanupSchedule((sp, opts) =>
        {
            PgOutboxCleanupSettings settings = sp.GetRequiredService<PgOutboxCleanupSettings>();
            opts.AsJob = settings.AsJob;
            opts.ExecutionInterval = settings.ExecutionInterval;
            opts.DropPartsAfterRetention = settings.DropPartsAfterRetention;
        })
        ;

        return services;
    }
}
