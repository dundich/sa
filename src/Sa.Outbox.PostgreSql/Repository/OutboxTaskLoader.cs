using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Classes;
using Sa.Data.PostgreSql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed record ConsumerGroupIdentifier(string ConsumerGroupId, int TenantId);

internal sealed partial class OutboxTaskLoader(
    IPgDataSource pg,
    SqlOutboxTemplate sql,
    IOutboxPartRepository partitionManager,
    ILogger<OutboxTaskLoader>? logger) : IOutboxTaskLoader
{

    public async Task<LoadGroupResult> LoadGroupBatch(
        OutboxMessageFilter filter,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1) return LoadGroupResult.Empty;


        try
        {
            await partitionManager.EnsureTaskParts(
                [new OutboxPartInfo(filter.TenantId, filter.ConsumerGroupId, filter.NowDate)],
                cancellationToken);

            return await LoadGroupAndShiftOffsetWithRetry(filter, batchSize, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            LogCanceledLoad(ex, filter);
            return LoadGroupResult.Empty;
        }
        catch (Exception ex) when (!ex.IsCritical())
        {
            LogErrorLoad(ex, filter);
            return LoadGroupResult.Empty;
        }
    }

    private async Task<LoadGroupResult> LoadGroupAndShiftOffsetWithRetry(
        OutboxMessageFilter filter,
        int batchSize,
        CancellationToken cancellationToken)
    {
        return await Retry.Jitter(
            async t => await LoadGroupAndShiftOffset(filter, batchSize, cancellationToken)
            , next: (ex, i) => (ex is NpgsqlException exception) && exception.IsTransient
            , cancellationToken: cancellationToken);
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


        Guid currentOffset = await GetCurrentOffset(consumerGroup, conn, tx, cancellationToken);

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
        Guid currentOffset,
        OutboxMessageFilter filter,
        int batchSize,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlLoadConsumerGroup, conn, tx);


        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, filter.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.MsgPart, filter.Part));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, filter.ConsumerGroupId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(SqlParam.Offset, currentOffset));
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.Limit, batchSize));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.NowDate, filter.NowDate.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.FromDate, filter.FromDate.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.ToDate, filter.ToDate.ToUnixTimeSeconds()));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            if (await reader.IsDBNullAsync(0, cancellationToken))
                return LoadGroupResult.Empty;

            int copiedRows = reader.GetInt32(0);
            if (copiedRows == 0)
                return LoadGroupResult.Empty;

            return new LoadGroupResult(copiedRows, reader.GetGuid(1));
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
        using var lockCmd = new NpgsqlCommand(sql.SqlLockOffset, conn, tx);
        int lockKey = CalculateLockKey(consumerGroup);
        lockCmd.Parameters.AddWithValue(SqlParam.OffsetKey, lockKey);
        await lockCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateOffsetAsync(
        ConsumerGroupIdentifier consumerGroup,
        Guid newOffset,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand(sql.SqlUpdateOffset, conn, tx);

        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, consumerGroup.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, consumerGroup.ConsumerGroupId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(SqlParam.Offset, newOffset));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Guid> GetCurrentOffset(
        ConsumerGroupIdentifier consumerGroup,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlSelectOffset, conn, tx);

        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, consumerGroup.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, consumerGroup.ConsumerGroupId));

        object? currentOffsetObj = await command.ExecuteScalarAsync(cancellationToken);

        Guid currentOffset = (currentOffsetObj == null)
            ? await InitializeOffset(consumerGroup, conn, tx, cancellationToken)
            : (Guid)currentOffsetObj;

        return currentOffset;
    }

    private async Task<Guid> InitializeOffset(
        ConsumerGroupIdentifier consumerGroup,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlInitOffset, conn, tx);

        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, consumerGroup.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, consumerGroup.ConsumerGroupId));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return Guid.Empty;
    }


    public static int CalculateLockKey(ConsumerGroupIdentifier consumerGroup)
    {
        uint hash = 0xffffffff;
        foreach (char c in consumerGroup.ToString())
        {
            hash ^= c;
            for (int i = 0; i < 8; i++)
            {
                hash = hash >> 1 ^ ((hash & 1) != 0 ? 0xA6BCD5B9u : 0);
            }
        }
        return (int)(hash ^ 0xffffffff);
    }


    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "Load consumer group cancelled for filter: {Filter}")]
    partial void LogCanceledLoad(Exception exception, OutboxMessageFilter filter);


    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Error,
        Message = "Error loading consumer group for filter: {Filter}")]
    partial void LogErrorLoad(Exception exception, OutboxMessageFilter filter);

}
