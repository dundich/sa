using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;
using System.Collections.Concurrent;
using System.Data;

namespace Sa.Outbox.PostgreSql.Services;

/// <summary>
///  Подгружаем новые задания для консьюмера из таблицы вх. сообщений _msg$
/// </summary>
internal sealed partial class OutboxTaskLoader(
    IPgDataSource pg,
    SqlOutboxBuilder sql,
    PgOutboxConsumeSettings consumeSettings,
    IOutboxTypeResolver hashResolver,
    ILogger<OutboxTaskLoader>? logger = null) : IOutboxTaskLoader
{

    private readonly ILogger _logger = logger ?? NullLogger<OutboxTaskLoader>.Instance;

    /// <summary>
    /// Advisory-lock keys by consumer group. <see cref="CalculateLockKey"/> renders the record's
    /// <c>ToString()</c> and walks it byte by byte; the key is stable for a (group, tenant), so
    /// caching it removes that string from every poll. Bounded by configured groups × tenants.
    /// </summary>
    private readonly ConcurrentDictionary<ConsumerGroupIdentifier, int> _lockKeys = new();

    internal sealed record ConsumerGroupIdentifier(string ConsumerGroupId, int TenantId);

    public async Task<LoadGroupResult> LoadNewTasks(
        OutboxMessageFilter filter,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1) return LoadGroupResult.Empty;

        try
        {
            return await LoadGroupAndShiftOffsetWithRetry(filter, batchSize, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Do not turn a cancel into an empty queue: DeliveryJob would report idle and back
            // the poll off while the host is shutting down.
            LogCanceledLoad(_logger, ex, filter);
            throw;
        }
        catch (Exception ex) when (!ex.IsCritical())
        {
            // PgRetryStrategy has already retried transients. Swallowing what remains made a
            // broken load look like an empty queue: the cursor did not advance (no message
            // loss) but adaptive idle backed off, and the only signal was this log line.
            LogErrorLoad(_logger, ex, filter);
            throw;
        }
    }

    private async Task<LoadGroupResult> LoadGroupAndShiftOffsetWithRetry(
        OutboxMessageFilter filter,
        int batchSize,
        CancellationToken cancellationToken)
    {
        return await PgRetryStrategy.ExecuteWithRetry(
            async t => await LoadGroupAndShiftOffset(filter, batchSize, t),
            cancellationToken: cancellationToken);
    }

    private async Task<LoadGroupResult> LoadGroupAndShiftOffset(
        OutboxMessageFilter filter,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var conn = await pg.OpenDbConnection(cancellationToken);

        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        ConsumerGroupIdentifier consumerGroup = new(filter.ConsumerGroupId, filter.TenantId);

        await AcquireOffsetLock(consumerGroup, conn, tx, cancellationToken);


        long currentOffset = await GetCurrentOffset(consumerGroup, conn, tx, cancellationToken);

        var batchResult = await LoadTasksByOffset(currentOffset, filter, batchSize, conn, tx, cancellationToken);

        if (!batchResult.IsEmpty() && batchResult.NewOffset != currentOffset)
        {
            await UpdateOffsetAsync(consumerGroup, batchResult.NewOffset, conn, tx, cancellationToken);
        }

        // COMMIT — unlock advisory lock!
        await tx.CommitAsync(cancellationToken);
        return batchResult;
    }

    private async Task<LoadGroupResult> LoadTasksByOffset(
        long currentOffset,
        OutboxMessageFilter filter,
        int batchSize,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetHashCode(filter.PayloadType, cancellationToken);

        await using var command = new NpgsqlCommand(sql.SqlLoadConsumerGroup, conn, tx);

        command
            .AddParamTenantId(filter.TenantId)
            .AddParamMsgPart(filter.Part)
            .AddParamConsumerGroupId(filter.ConsumerGroupId)
            .AddParamOffset(currentOffset)
            .AddParamLimit(batchSize)
            .AddParamNowDate(filter.NowDate)
            .AddParamWindowFrom(filter.FromDate)
            .AddParamWindowTo(filter.ToDate)
            .AddParamTypeId(typeCode)
            ;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            // COUNT(*) is bigint. GetInt32 throws InvalidCastException on Npgsql 10, and the
            // caller used to swallow that as an empty queue — no task was ever created.
            // COUNT(*) is never NULL; the empty-insert case is a zero count, and only then is
            // max_seq (column 1) NULL, so it must not be read.
            long copiedRows = reader.GetInt64(0);
            if (copiedRows == 0)
                return LoadGroupResult.Empty;

            return new LoadGroupResult(checked((int)copiedRows), reader.GetInt64(1));
        }
        else
        {
            return LoadGroupResult.Empty;
        }
    }

    private async Task AcquireOffsetLock(
        ConsumerGroupIdentifier consumerGroup,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        int lockKey = _lockKeys.GetOrAdd(consumerGroup, static group => CalculateLockKey(group));

        using var command = new NpgsqlCommand(sql.SqlLockOffset, conn, tx);
        command.AddParamAdvisoryXactLock(lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateOffsetAsync(
        ConsumerGroupIdentifier consumerGroup,
        long newOffset,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand(sql.SqlUpdateOffset, conn, tx);

        command
            .AddParamTenantId(consumerGroup.TenantId)
            .AddParamConsumerGroupId(consumerGroup.ConsumerGroupId)
            .AddParamOffset(newOffset);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> GetCurrentOffset(
        ConsumerGroupIdentifier consumerGroup,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlSelectOffset, conn, tx);

        command
            .AddParamTenantId(consumerGroup.TenantId)
            .AddParamConsumerGroupId(consumerGroup.ConsumerGroupId);

        object? currentOffsetObj = await command.ExecuteScalarAsync(cancellationToken);

        long minOffset = await ResolveMinOffsetAsync(consumerGroup, conn, tx, cancellationToken);

        long currentOffset = (currentOffsetObj != null)
            ? (long)currentOffsetObj
            : await InitializeOffset(consumerGroup, minOffset, conn, tx, cancellationToken);

        return (currentOffset > minOffset) ? currentOffset : minOffset;
    }

    /// <summary>
    /// Translates a configured floor into the exclusive <c>msg_seq</c> boundary below it — the
    /// seq of the last message that still sorts below the floor (0 when none) — once per
    /// (consumer group, tenant), and keeps the result cached in <see cref="PgOutboxConsumeSettings"/>.
    /// The resolving queries filter by <c>tenant_id</c>, so a floor cached for one tenant is not
    /// a floor for another. Runs inside the advisory lock so two workers of the same group and
    /// tenant cannot resolve different floors.
    /// <c>SqlLoadConsumerGroup</c> compares <c>msg_seq &gt; @offset</c> against this boundary, so
    /// the first message at or above the floor is the first delivered (strict semantics, same set
    /// the old UUID cursor picked with <c>msg_id &gt; @offset</c>).
    /// </summary>
    private async Task<long> ResolveMinOffsetAsync(
        ConsumerGroupIdentifier consumerGroup,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        var groupId = consumerGroup.ConsumerGroupId;

        if (consumeSettings.TryGetResolvedFloor(groupId, consumerGroup.TenantId, out long cached))
            return cached;

        long floor = 0;

        if (consumeSettings.TryGetGuidFloor(groupId, out Guid guidFloor))
        {
            using var command = new NpgsqlCommand(sql.SqlResolveMsgIdFloor, conn, tx);

            command
                .AddParamTenantId(consumerGroup.TenantId)
                .AddParamMsgId(guidFloor);

            floor = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        else if (consumeSettings.TryGetDateFloor(groupId, out DateTimeOffset dateFloor))
        {
            using var command = new NpgsqlCommand(sql.SqlResolveDateFloor, conn, tx);

            command
                .AddParamTenantId(consumerGroup.TenantId)
                .AddParamFloorDate(dateFloor);

            floor = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        consumeSettings.SetResolvedFloor(groupId, consumerGroup.TenantId, floor);
        return floor;
    }

    private async Task<long> InitializeOffset(
        ConsumerGroupIdentifier consumerGroup,
        long minOffset,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlInitOffset, conn, tx);

        command
            .AddParamTenantId(consumerGroup.TenantId)
            .AddParamOffset(minOffset)
            .AddParamConsumerGroupId(consumerGroup.ConsumerGroupId);

        await command.ExecuteNonQueryAsync(cancellationToken);

        return minOffset;
    }


    public static int CalculateLockKey(ConsumerGroupIdentifier consumerGroup)
        => PgAdvisoryKeys.Fnv(consumerGroup.ToString());


    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "Load consumer group cancelled for filter: {Filter}")]
    static partial void LogCanceledLoad(ILogger logger, Exception exception, OutboxMessageFilter filter);


    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Error,
        Message = "Error loading consumer group for filter: {Filter}")]
    static partial void LogErrorLoad(ILogger logger, Exception exception, OutboxMessageFilter filter);

}
