using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// The PostgreSQL implementation of <see cref="IPgOutboxMessageMaintenance"/>: resets settled task rows
/// so the delivery pipeline picks them up again.
/// </summary>
internal sealed class PgOutboxMessageMaintenance(
    IPgDataSource dataSource,
    SqlOutboxBuilder sql,
    PgOutboxTableSettings settings,
    TimeProvider? timeProvider = null) : IPgOutboxMessageMaintenance
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<int> RequeueAsync(
        string payloadId,
        int tenantId,
        string? consumerGroup = null,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadId);
        if (consumerGroup is not null) ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        if (part is not null) ArgumentException.ThrowIfNullOrWhiteSpace(part);

        (DateTimeOffset windowFrom, DateTimeOffset windowTo) =
            TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        return await ExecuteAsync(
            sql.SqlRequeueByPayload,
            command =>
            {
                AddCommon(command, tenantId, windowFrom, windowTo);
                command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.PayloadId, payloadId));
                TrackingParameters.AddOptionalText(command, SqlParam.ConsumerGroupId, consumerGroup);
                TrackingParameters.AddOptionalText(command, SqlParam.MsgPart, part);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> RequeueByMessageIdAsync(
        Guid messageId,
        int tenantId,
        string? consumerGroup = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        if (consumerGroup is not null) ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);

        (DateTimeOffset windowFrom, DateTimeOffset windowTo) =
            TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        return await ExecuteAsync(
            sql.SqlRequeueByMessageId,
            command =>
            {
                AddCommon(command, tenantId, windowFrom, windowTo);
                command.Parameters.Add(new NpgsqlParameter<Guid>(SqlParam.MsgId, messageId));
                TrackingParameters.AddOptionalText(command, SqlParam.ConsumerGroupId, consumerGroup);

                // The template also carries the optional part filter; bind it as NULL. A v7 id is
                // unique across parts, so the filter would only add a predicate.
                TrackingParameters.AddOptionalText(command, SqlParam.MsgPart, null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> RequeueTaskAsync(
        long taskId,
        int tenantId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        (DateTimeOffset windowFrom, DateTimeOffset windowTo) =
            TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        return await ExecuteAsync(
            sql.SqlRequeueByTaskId,
            command =>
            {
                AddCommon(command, tenantId, windowFrom, windowTo);
                command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.TaskId, taskId));

                // The task id is unique and the group is implied by the row; the template's optional
                // filters are bound as NULL.
                TrackingParameters.AddOptionalText(command, SqlParam.ConsumerGroupId, null);
                TrackingParameters.AddOptionalText(command, SqlParam.MsgPart, null);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void AddCommon(
        NpgsqlCommand command,
        int tenantId,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo)
    {
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));
    }

    private async ValueTask<int> ExecuteAsync(
        string query,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);

        // No task table means no group has ever loaded anything: there is nothing to requeue.
        if (!tables.Task) return 0;

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(query, connection);
        bind(command);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
