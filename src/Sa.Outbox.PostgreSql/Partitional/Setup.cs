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
                    .PartByList(tableSettings.Message.Fields.TenantId, tableSettings.Message.Fields.MsgPart)
                    .TimestampAs(tableSettings.Message.Fields.MsgCreatedAt)
                    .WithFillFactor(tableSettings.Message.FillFactor) // insert only
                    .AddPostSql(() => sql.SqlCreateTypeTable)
                ;

                ITableBuilder queueTableBuilder = schema
                    .AddTable(tableSettings.TaskQueue.TableName, tableSettings.TaskQueue.Fields.All())
                    .PartByList(tableSettings.TaskQueue.Fields.TenantId, tableSettings.TaskQueue.Fields.ConsumerGroup)
                    .TimestampAs(tableSettings.TaskQueue.Fields.TaskCreatedAt)
                    .WithFillFactor(tableSettings.TaskQueue.FillFactor)
                    .AddPostSql(() => sql.SqlCreateOffsetTable)
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
