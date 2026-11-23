using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// The PostgreSQL implementation of <see cref="IPgOutboxMessageTracker"/>: read-only queries over the
/// provider's own tables.
/// </summary>
/// <remarks>
/// <para>
/// The projection order in <see cref="SqlOutboxBuilder"/> is the reading contract — the reader binds
/// by ordinal, so the two must change together. Aliases only make the SQL self-documenting (and avoid
/// ambiguous duplicate column names in a self-join).
/// </para>
/// <para>
/// Nothing here retries. These are diagnostic reads: the caller decides whether a transient failure
/// is worth repeating, and the provider's <c>PgRetryStrategy</c> exists for the delivery path, which
/// must not lose a batch.
/// </para>
/// </remarks>
internal sealed class PgOutboxMessageTracker(
    IPgDataSource dataSource,
    SqlOutboxBuilder sql,
    PgOutboxTableSettings settings,
    TimeProvider? timeProvider = null) : IPgOutboxMessageTracker
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<IReadOnlyList<PgOutboxMessageTrace>> GetAsync(
        string payloadId,
        int tenantId,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        // An empty payload id is what the provider stores for a message without one; matching every
        // such message is never what the caller meant.
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadId);
        if (part is not null) ArgumentException.ThrowIfNullOrWhiteSpace(part);

        (DateTimeOffset windowFrom, DateTimeOffset windowTo) = TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);
        if (!tables.Message) return [];

        string query = (part is null, tables.Task) switch
        {
            (true, true) => sql.SqlTrackMessagesByPayload,
            (false, true) => sql.SqlTrackMessagesByPayloadAndPart,
            (true, false) => sql.SqlTrackMessagesByPayloadNoTask,
            (false, false) => sql.SqlTrackMessagesByPayloadAndPartNoTask,
        };

        List<TraceRow> rows = await ReadTracesAsync(
            query,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));
                command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.PayloadId, payloadId));
                command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
                command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));

                if (part is not null)
                    command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.MsgPart, part));
            },
            cancellationToken).ConfigureAwait(false);

        return await ToTracesAsync(rows, tables, windowFrom, windowTo, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<PgOutboxMessageTrace>> GetManyAsync(
        IReadOnlyCollection<string> payloadIds,
        int tenantId,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloadIds);
        if (part is not null) ArgumentException.ThrowIfNullOrWhiteSpace(part);

        // Blank ids are what the provider stores for a message without one; matching them would pull
        // in unrelated messages. Duplicates would only repeat the same predicate value.
        string[] ids =
        [
            .. payloadIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal),
        ];

        if (ids.Length == 0) return [];

        (DateTimeOffset windowFrom, DateTimeOffset windowTo) =
            TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);

        if (!tables.Message) return [];

        string query = (part is null, tables.Task) switch
        {
            (true, true) => sql.SqlTrackMessagesByPayloads,
            (false, true) => sql.SqlTrackMessagesByPayloadsAndPart,
            (true, false) => sql.SqlTrackMessagesByPayloadsNoTask,
            (false, false) => sql.SqlTrackMessagesByPayloadsAndPartNoTask,
        };

        List<TraceRow> rows = await ReadTracesAsync(
            query,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));
                command.Parameters.Add(new NpgsqlParameter<string[]>(SqlParam.PayloadIds, ids));
                command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
                command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));

                if (part is not null)
                    command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.MsgPart, part));
            },
            cancellationToken).ConfigureAwait(false);

        return await ToTracesAsync(rows, tables, windowFrom, windowTo, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PgOutboxMessageTrace?> GetByMessageIdAsync(
        Guid messageId,
        int tenantId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        (DateTimeOffset windowFrom, DateTimeOffset windowTo) = TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);
        if (!tables.Message) return null;

        List<TraceRow> rows = await ReadTracesAsync(
            tables.Task ? sql.SqlTrackMessageById : sql.SqlTrackMessageByIdNoTask,
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));
                command.Parameters.Add(new NpgsqlParameter<Guid>(SqlParam.MsgId, messageId));
                command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
                command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));
            },
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PgOutboxMessageTrace> traces =
            await ToTracesAsync(rows, tables, windowFrom, windowTo, cancellationToken).ConfigureAwait(false);

        return traces.Count == 0 ? null : traces[0];
    }

    public async ValueTask<IReadOnlyList<PgOutboxConsumerGroup>> GetConsumerGroupsAsync(
        int tenantId,
        CancellationToken cancellationToken = default)
    {
        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);

        if (!tables.Offset) return [];

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql.SqlTrackConsumerGroups, connection);
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));

        List<PgOutboxConsumerGroup> groups = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(new PgOutboxConsumerGroup
            {
                // The column is nullable in the DDL, although it is half of the primary key.
                ConsumerGroup = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                TenantId = reader.GetInt32(1),
                OffsetSeq = reader.GetInt64(2),
                UpdatedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            });
        }

        return groups;
    }

    public async ValueTask<IReadOnlyList<PgOutboxStatusCount>> CountAsync(
        int tenantId,
        string? consumerGroup = null,
        string? part = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        if (consumerGroup is not null) ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        if (part is not null) ArgumentException.ThrowIfNullOrWhiteSpace(part);

        (DateTimeOffset windowFrom, DateTimeOffset windowTo) =
            TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);

        if (!tables.Task) return [];

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql.SqlTrackCounts, connection);
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(
            SqlParam.NowDate,
            _timeProvider.GetUtcNow().ToUnixTimeSeconds()));
        TrackingParameters.AddOptionalText(command, SqlParam.ConsumerGroupId, consumerGroup);
        TrackingParameters.AddOptionalText(command, SqlParam.MsgPart, part);

        List<PgOutboxStatusCount> counts = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts.Add(new PgOutboxStatusCount
            {
                ConsumerGroup = reader.GetString(0),
                StatusCode = (DeliveryStatusCode)reader.GetInt32(1),
                Count = reader.GetInt64(2),
                Stuck = reader.GetInt64(3),
            });
        }

        return counts;
    }

    public async ValueTask<PgOutboxMessagePayload?> GetPayloadAsync(
        Guid messageId,
        int tenantId,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        (DateTimeOffset windowFrom, DateTimeOffset windowTo) =
            TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);

        if (!tables.Message) return null;

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            tables.Type ? sql.SqlTrackPayload : sql.SqlTrackPayloadNoType,
            connection);

        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(SqlParam.MsgId, messageId));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        return new PgOutboxMessagePayload
        {
            MessageId = reader.GetGuid(0),
            PayloadTypeId = reader.GetInt64(1),
            PayloadType = reader.IsDBNull(2) ? null : reader.GetString(2),
            Size = reader.GetInt32(3),
            Payload = reader.IsDBNull(4)
                ? ReadOnlyMemory<byte>.Empty
                : new ReadOnlyMemory<byte>(reader.GetFieldValue<byte[]>(4)),
        };
    }

    public async ValueTask<IReadOnlyList<PgOutboxTaskEntry>> SearchAsync(
        PgOutboxTaskQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Part is not null) ArgumentException.ThrowIfNullOrWhiteSpace(query.Part);
        if (query.ConsumerGroup is not null) ArgumentException.ThrowIfNullOrWhiteSpace(query.ConsumerGroup);

        if (query.Limit is < 1 or > PgOutboxTaskQuery.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.Limit,
                $"query.Limit must be between 1 and {PgOutboxTaskQuery.MaxLimit}.");
        }

        (DateTimeOffset windowFrom, DateTimeOffset windowTo) =
            TrackingWindow.Resolve(_timeProvider, query.FromDate, query.ToDate);

        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);

        if (!tables.Task) return [];

        // An empty status set must match nothing, not everything: it can only be a caller mistake, and
        // silently dropping the filter would return the whole queue instead.
        int[]? statuses = query.Statuses is null
            ? null
            : [.. query.Statuses.Select(s => (int)s).Distinct()];

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql.SqlTrackSearch, connection);
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, query.TenantId));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.Limit, query.Limit));

        TrackingParameters.AddOptionalText(command, SqlParam.ConsumerGroupId, query.ConsumerGroup);
        TrackingParameters.AddOptionalText(command, SqlParam.MsgPart, query.Part);

        command.Parameters.Add(new NpgsqlParameter(SqlParam.Statuses, NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = statuses ?? (object)DBNull.Value,
        });

        // Typed nullable longs: the statement guards the cursor comparison with `@cur_seq IS NULL`, and
        // an untyped null cannot be bound to a bigint comparison.
        command.Parameters.Add(new NpgsqlParameter<long?>(SqlParam.CursorSeq, query.After?.MsgSeq));
        command.Parameters.Add(new NpgsqlParameter<long?>(SqlParam.CursorTask, query.After?.TaskId));

        long nowUnix = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        List<PgOutboxTaskEntry> entries = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var status = (DeliveryStatusCode)reader.GetInt32(8);
            long lockExpiresOn = reader.IsDBNull(12) ? 0 : reader.GetInt64(12);
            long errorId = reader.IsDBNull(13) ? 0 : reader.GetInt64(13);

            entries.Add(new PgOutboxTaskEntry
            {
                TaskId = reader.GetInt64(0),
                ConsumerGroup = reader.GetString(1),
                MessageId = reader.GetGuid(2),
                MsgSeq = reader.GetInt64(3),
                PayloadId = reader.GetString(4),
                TenantId = query.TenantId,
                Part = reader.GetString(5),
                MessageCreatedAt = reader.GetInt64(6).ToDateTimeOffsetFromUnixTimestamp(),
                TaskCreatedAt = reader.GetInt64(7).ToDateTimeOffsetFromUnixTimestamp(),
                StatusCode = status,
                StatusMessage = reader.GetString(9),
                Attempt = reader.GetInt32(10),
                DeliveredAt = ReadOptionalTimestamp(reader, 11),
                LockExpiresOn = lockExpiresOn > 0
                    ? lockExpiresOn.ToDateTimeOffsetFromUnixTimestamp()
                    : null,
                ErrorId = errorId > 0 ? errorId : null,
                IsStuck = status == DeliveryStatusCode.Processing && lockExpiresOn < nowUnix,
            });
        }

        return entries;
    }

    public async ValueTask<IReadOnlyList<PgOutboxDeliveryAttempt>> GetHistoryAsync(
        string payloadId,
        int tenantId,
        string? consumerGroup = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadId);
        if (consumerGroup is not null) ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        (DateTimeOffset windowFrom, DateTimeOffset windowTo) = TrackingWindow.Resolve(_timeProvider, fromDate, toDate);

        PgOutboxStorageTables tables = await PgOutboxStorageTables
            .LoadAsync(dataSource, sql, settings, cancellationToken)
            .ConfigureAwait(false);
        if (!tables.Delivery) return [];

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql.SqlTrackHistory, connection);
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, tenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.PayloadId, payloadId));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, windowFrom.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, windowTo.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.Limit, limit));

        // Explicit NpgsqlDbType: an untyped NULL parameter cannot be bound to the `@gr IS NULL`
        // predicate, and Npgsql refuses to infer the type of a null value.
        command.Parameters.Add(new NpgsqlParameter(SqlParam.ConsumerGroupId, NpgsqlDbType.Text)
        {
            Value = consumerGroup ?? (object)DBNull.Value,
        });

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AttemptRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AttemptRow(
                DeliveryId: reader.GetInt64(0),
                ConsumerGroup: reader.GetString(1),
                TaskId: reader.GetInt64(2),
                StatusCode: (DeliveryStatusCode)reader.GetInt32(3),
                StatusMessage: reader.GetString(4),
                CreatedAt: reader.GetInt64(5).ToDateTimeOffsetFromUnixTimestamp(),
                ErrorId: reader.GetInt64(6)));
        }

        // The statement returns newest first so LIMIT keeps the recent attempts; the caller gets a
        // timeline, oldest first.
        rows.Reverse();

        Dictionary<long, ErrorDetail> errors = await ResolveErrorsAsync(
            [.. rows.Select(r => r.ErrorId).Where(id => id > 0).Distinct()],
            windowFrom,
            windowTo,
            tables,
            cancellationToken).ConfigureAwait(false);

        PgOutboxDeliveryAttempt[] attempts = new PgOutboxDeliveryAttempt[rows.Count];

        for (int i = 0; i < rows.Count; i++)
        {
            AttemptRow row = rows[i];
            errors.TryGetValue(row.ErrorId, out ErrorDetail? error);

            attempts[i] = new PgOutboxDeliveryAttempt
            {
                DeliveryId = row.DeliveryId,
                ConsumerGroup = row.ConsumerGroup,
                TaskId = row.TaskId,
                StatusCode = row.StatusCode,
                StatusMessage = row.StatusMessage,
                CreatedAt = row.CreatedAt,
                ErrorId = row.ErrorId > 0 ? row.ErrorId : null,
                ErrorType = error?.Type,
                ErrorMessage = error?.Message,
            };
        }

        return attempts;
    }

    /// <summary>
    /// Reads the tracking projection. The SELECT list is fixed by
    /// <see cref="SqlOutboxBuilder.SqlTrackMessagesByPayload"/>, so the columns are bound by ordinal —
    /// the same reason <see cref="Commands.NpgsqlOutboxReader"/> captures ordinals instead of looking
    /// them up per row.
    /// </summary>
    private async Task<List<TraceRow>> ReadTracesAsync(
        string query,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(query, connection);
        bind(command);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        List<TraceRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TraceRow(
                MessageId: reader.GetGuid(0),
                MsgSeq: reader.GetInt64(1),
                PayloadId: reader.GetString(2),
                TenantId: reader.GetInt32(3),
                Part: reader.GetString(4),
                CreatedAt: reader.GetInt64(5).ToDateTimeOffsetFromUnixTimestamp(),
                ConsumerGroup: reader.IsDBNull(6) ? null : reader.GetString(6),
                TaskId: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                StatusCode: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                StatusMessage: reader.IsDBNull(9) ? null : reader.GetString(9),
                Attempt: reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                TaskCreatedAt: reader.IsDBNull(11)
                    ? null
                    : reader.GetInt64(11).ToDateTimeOffsetFromUnixTimestamp(),
                DeliveredAt: ReadOptionalTimestamp(reader, 12),

                // Raw, unlike the delivery timestamp: 0 means "no lease recorded", but for a task
                // that is still Processing it is also an already-expired lease, so IsStuck needs
                // the number rather than a null.
                LockExpiresOn: reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
                ErrorId: reader.IsDBNull(14) ? 0 : reader.GetInt64(14)));
        }

        return rows;
    }

    /// <summary>
    /// The task columns are <c>NOT NULL DEFAULT 0</c>, and 0 means "never" for both the delivery
    /// timestamp and the lease. A LEFT JOIN also yields NULL for a message without a task row, so both
    /// cases must read as "absent" rather than as 1970-01-01.
    /// </summary>
    private static DateTimeOffset? ReadOptionalTimestamp(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;

        long value = reader.GetInt64(ordinal);

        return value == 0 ? null : value.ToDateTimeOffsetFromUnixTimestamp();
    }

    private async Task<IReadOnlyList<PgOutboxMessageTrace>> ToTracesAsync(
        List<TraceRow> rows,
        PgOutboxStorageTables tables,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];

        Dictionary<long, ErrorDetail> errors = await ResolveErrorsAsync(
            [.. rows.Select(r => r.ErrorId).Where(id => id > 0).Distinct()],
            windowFrom,
            windowTo,
            tables,
            cancellationToken).ConfigureAwait(false);

        // A status is "rentable" exactly while the provider's lock-and-select query still lists it
        // (SqlOutboxBuilder.LockAndSelectStatusCodes); everything else is terminal until a requeue.
        long nowUnix = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        List<TraceBucket> buckets = [];
        Dictionary<Guid, TraceBucket> byMessageId = [];

        foreach (TraceRow row in rows)
        {
            if (!byMessageId.TryGetValue(row.MessageId, out TraceBucket? bucket))
            {
                bucket = new TraceBucket(row);
                byMessageId.Add(row.MessageId, bucket);
                buckets.Add(bucket);
            }

            if (row.ConsumerGroup is null) continue;

            var status = (DeliveryStatusCode)(row.StatusCode ?? (int)DeliveryStatusCode.Pending);
            errors.TryGetValue(row.ErrorId, out ErrorDetail? error);

            bucket.Deliveries.Add(new PgOutboxDeliveryState
            {
                ConsumerGroup = row.ConsumerGroup,
                TaskId = row.TaskId,
                StatusCode = status,
                StatusMessage = row.StatusMessage ?? string.Empty,
                Attempt = row.Attempt,
                TaskCreatedAt = row.TaskCreatedAt ?? row.CreatedAt,
                DeliveredAt = row.DeliveredAt,
                LockExpiresOn = row.LockExpiresOn > 0
                    ? row.LockExpiresOn.ToDateTimeOffsetFromUnixTimestamp()
                    : null,
                ErrorId = row.ErrorId > 0 ? row.ErrorId : null,
                ErrorType = error?.Type,
                ErrorMessage = error?.Message,

                // Processing with an expired lease is how a crashed worker looks: the next poll
                // re-rents the task. It is a diagnosis, not an error status.
                IsStuck = status == DeliveryStatusCode.Processing && row.LockExpiresOn < nowUnix,
            });
        }

        PgOutboxMessageTrace[] traces = new PgOutboxMessageTrace[buckets.Count];

        for (int i = 0; i < buckets.Count; i++)
        {
            TraceBucket bucket = buckets[i];

            traces[i] = new PgOutboxMessageTrace
            {
                MessageId = bucket.First.MessageId,
                MsgSeq = bucket.First.MsgSeq,
                PayloadId = bucket.First.PayloadId,
                TenantId = bucket.First.TenantId,
                Part = bucket.First.Part,
                CreatedAt = bucket.First.CreatedAt,
                Deliveries = bucket.Deliveries,
            };
        }

        return traces;
    }

    /// <summary>
    /// Resolves the <c>__error$</c> rows of the given ids. The id is a hash of the composed exception
    /// text, so rows sharing an id also share their type and message — the first row wins and the day
    /// of each row does not have to be matched exactly.
    /// </summary>
    private async Task<Dictionary<long, ErrorDetail>> ResolveErrorsAsync(
        long[] errorIds,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        PgOutboxStorageTables tables,
        CancellationToken cancellationToken)
    {
        if (errorIds.Length == 0 || !tables.Error) return [];

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql.SqlTrackErrors, connection);
        command.Parameters.Add(new NpgsqlParameter<long[]>(SqlParam.ErrorIds, errorIds));

        // One day of slack below the window: the error row is keyed by the day start of the delivery
        // timestamp, and a consumer reporting a non-UTC DateTimeOffset makes that instant differ from
        // the UTC midnight the reader computes. The upper bound is the window end (now, by default).
        command.Parameters.Add(new NpgsqlParameter<long>(
            SqlParam.WindowFrom,
            (windowFrom - TimeSpan.FromDays(1)).ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(
            SqlParam.WindowTo,
            windowTo.ToUnixTimeSeconds()));

        Dictionary<long, ErrorDetail> errors = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long errorId = reader.GetInt64(0);

            // Same id => same text => same type and message; a second row (another day) adds nothing.
            _ = errors.TryAdd(errorId, new ErrorDetail(
                Type: reader.GetString(1),
                Message: reader.GetString(2)));
        }

        return errors;
    }

    private sealed record ErrorDetail(string Type, string Message);

    private readonly record struct TraceRow(
        Guid MessageId,
        long MsgSeq,
        string PayloadId,
        int TenantId,
        string Part,
        DateTimeOffset CreatedAt,
        string? ConsumerGroup,
        long? TaskId,
        int? StatusCode,
        string? StatusMessage,
        int Attempt,
        DateTimeOffset? TaskCreatedAt,
        DateTimeOffset? DeliveredAt,
        long LockExpiresOn,
        long ErrorId);

    private readonly record struct AttemptRow(
        long DeliveryId,
        string ConsumerGroup,
        long TaskId,
        DeliveryStatusCode StatusCode,
        string StatusMessage,
        DateTimeOffset CreatedAt,
        long ErrorId);

    /// <summary>One message while its rows are being folded: the header plus the per-group states.</summary>
    private sealed class TraceBucket(TraceRow first)
    {
        public TraceRow First { get; } = first;

        public List<PgOutboxDeliveryState> Deliveries { get; } = [];
    }
}
