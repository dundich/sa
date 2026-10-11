using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Partitional;

internal static class Setup
{
    public static IServiceCollection AddOutboxPartitional(this IServiceCollection services, string scopeKey)
    {
        services.TryAddTransient<MsgMigrationSupport>();
        services.TryAddTransient<TaskMigrationSupport>();

        // Keyed module: the outbox tables, migration/cleanup settings and the migration/cleanup
        // jobs live in the outbox's own scope, never in the application's default partitional
        // module. The module falls back to the shared (unkeyed) outbox data source because it does
        // not pin a named one.
        _ = services.AddSaPartitional(scopeKey, (sp, builder) =>
        {
            SqlOutboxBuilder sql = sp.GetRequiredService<SqlOutboxBuilder>();
            var tableSettings = sql.TableSettings;

            builder.AddSchema(tableSettings.DatabaseSchemaName, schema =>
            {
                ITableBuilder outboxTableBuilder = schema
                    .AddTable(tableSettings.Message.TableName, tableSettings.Message.Fields.All())
                    .PartByList(tableSettings.Message.Fields.TenantId, tableSettings.Message.Fields.MsgPart)
                    .TimestampAs(tableSettings.Message.Fields.MsgCreatedAt)
                    .WithFillFactor(tableSettings.Message.FillFactor) // insert only
                    .AddPostSql(() => $"""
                        {sql.SqlCreateTypeTable}
                        {sql.SqlAddMsgSeqColumnToMsgTable}
                        """)
                ;

                ITableBuilder queueTableBuilder = schema
                    .AddTable(tableSettings.TaskQueue.TableName, tableSettings.TaskQueue.Fields.All())
                    .PartByList(tableSettings.TaskQueue.Fields.TenantId, tableSettings.TaskQueue.Fields.ConsumerGroup)
                    .TimestampAs(tableSettings.TaskQueue.Fields.TaskCreatedAt)
                    .WithFillFactor(tableSettings.TaskQueue.FillFactor)
                    .AddPostSql(() => $"""
                        {sql.SqlCreateOffsetTable}
                        {sql.SqlAddMsgSeqColumnToTaskTable}
                        {sql.SqlAddGroupOffsetSeqColumnToOffsetTable}
                        """)
                ;

                ITableBuilder deliveryTableBuilder = schema
                    .AddTable(tableSettings.Delivery.TableName, tableSettings.Delivery.Fields.All())
                    .PartByList(tableSettings.Delivery.Fields.TenantId, tableSettings.Delivery.Fields.ConsumerGroup)
                    .TimestampAs(tableSettings.Delivery.Fields.DeliveryCreatedAt)
                    .WithFillFactor(tableSettings.Delivery.FillFactor)
                ;

                ITableBuilder errorTableBuilder = schema
                    .AddTable(tableSettings.Error.TableName, tableSettings.Error.Fields.All())
                    .TimestampAs(tableSettings.Error.Fields.ErrorCreatedAt)
                    .WithFillFactor(tableSettings.Error.FillFactor)
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
            opts.AsBackgroundJob = settings.AsBackgroundJob;
            opts.ExecutionInterval = settings.ExecutionInterval;
            opts.ForwardDays = settings.ForwardDays;
        })
        .AddPartCleanupSchedule((sp, opts) =>
        {
            PgOutboxCleanupSettings settings = sp.GetRequiredService<PgOutboxCleanupSettings>();
            opts.AsBackgroundJob = settings.AsBackgroundJob;
            opts.ExecutionInterval = settings.ExecutionInterval;
            opts.DropPartsAfterRetention = settings.DropPartsAfterRetention;
        })
        ;

        return services;
    }
}
