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


            builder.AddSchema(sql.DatabaseSchemaName, schema =>
            {
                ITableBuilder outboxTableBuilder = schema
                    .AddTable(sql.DatabaseMsgTableName, SqlOutboxTemplate.MsgFields)
                    .PartByList("tenant_id", "msg_part")
                    .TimestampAs("msg_created_at")
                    .WithFillFactor(100) // insert only
                    .AddPostSql(() => sql.SqlCreateTypeTable)
                ;

                ITableBuilder queueTableBuilder = schema
                    .AddTable(sql.DatabaseTaskTableName, SqlOutboxTemplate.TaskQueueFields)
                    .PartByList("tenant_id", "consumer_group")
                    .TimestampAs("task_created_at")
                    .WithFillFactor(60)
                    .AddPostSql(() => sql.SqlCreateOffsetTable)
                ;

                ITableBuilder deliveryTableBuilder = schema
                    .AddTable(sql.DatabaseDeliveryTableName, SqlOutboxTemplate.DeliveryFields)
                    .PartByList("tenant_id", "consumer_group")
                    .TimestampAs("delivery_created_at")
                    .WithFillFactor(100)
                ;

                ITableBuilder errorTableBuilder = schema
                    .AddTable(sql.DatabaseErrorTableName, SqlOutboxTemplate.ErrorFields)
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
