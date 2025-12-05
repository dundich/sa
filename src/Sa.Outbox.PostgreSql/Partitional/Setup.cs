using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Partitional;

internal static class Setup
{
    public static IServiceCollection AddOutboxPartitional(this IServiceCollection services)
    {
        services.TryAddTransient<OutboxMigrationSupport>();
        services.TryAddTransient<TaskMigrationSupport>();

        services.AddPartitional((sp, builder) =>
        {
            SqlOutboxTemplate sql = sp.GetRequiredService<SqlOutboxTemplate>();


            builder.AddSchema(sql.DatabaseSchemaName, schema =>
            {
                ITableBuilder outboxTableBuilder = schema
                    .AddTable(sql.DatabaseOutboxTableName, SqlOutboxTemplate.OutboxFields)
                    .PartByList("outbox_tenant", "outbox_part")
                    .TimestampAs("outbox_created_at")
                    .AddPostSql(() => sql.SqlCreateTypeTable)
                ;

                ITableBuilder taskTableBuilder = schema
                    .AddTable(sql.DatabaseTaskTableName, SqlOutboxTemplate.TaskFields)
                    .PartByList("outbox_tenant", "outbox_group_id")
                    .TimestampAs("task_created_at")
                    .AddPostSql(() => sql.SqlCreateOffsetTable)
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


                outboxTableBuilder.AddMigration(sp.GetRequiredService<OutboxMigrationSupport>());
                taskTableBuilder.AddMigration(sp.GetRequiredService<TaskMigrationSupport>());
                deliveryTableBuilder.AddMigration(sp.GetRequiredService<TaskMigrationSupport>());
                errorTableBuilder.AddMigration();
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
