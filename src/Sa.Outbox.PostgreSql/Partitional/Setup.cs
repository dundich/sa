using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Partitional;

internal static class Setup
{
    public static IServiceCollection AddOutboxPartitional(this IServiceCollection services)
    {
        services.TryAddTransient<MsgMigrationSupport>();
        services.TryAddTransient<TaskMigrationSupport>();

        _ = services.AddPartitional((sp, builder) =>
        {
            SqlOutboxTemplate sql = sp.GetRequiredService<SqlOutboxTemplate>();
            var tableSettings = sql.Settings;

            builder.AddSchema(sql.Settings.DatabaseSchemaName, schema =>
            {
                ITableBuilder outboxTableBuilder = schema
                    .AddTable(tableSettings.Message.TableName, tableSettings.Message.Fields.All())
                    .PartByList("tenant_id", "msg_part")
                    .TimestampAs("msg_created_at")
                    .WithFillFactor(100) // insert only
                    .AddPostSql(() => sql.SqlCreateTypeTable)
                ;

                ITableBuilder queueTableBuilder = schema
                    .AddTable(tableSettings.TaskQueue.TableName, tableSettings.TaskQueue.Fields.All())
                    .PartByList("tenant_id", "consumer_group")
                    .TimestampAs("task_created_at")
                    .WithFillFactor(50)
                    .AddPostSql(() => sql.SqlCreateOffsetTable)
                ;

                ITableBuilder deliveryTableBuilder = schema
                    .AddTable(tableSettings.Delivery.TableName, tableSettings.Delivery.Fields.All())
                    .PartByList("tenant_id", "consumer_group")
                    .TimestampAs("delivery_created_at")
                    .WithFillFactor(100)
                ;

                ITableBuilder errorTableBuilder = schema
                    .AddTable(tableSettings.Error.TableName, tableSettings.Error.Fields.All())
                    .TimestampAs("error_created_at")
                    .WithFillFactor(100)
                ;


                outboxTableBuilder.AddMigration(sp.GetRequiredService<MsgMigrationSupport>());
                queueTableBuilder.AddMigration(sp.GetRequiredService<TaskMigrationSupport>());
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
