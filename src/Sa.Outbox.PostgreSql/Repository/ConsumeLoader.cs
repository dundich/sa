using System.Data;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class ConsumeLoader(
    IPgDataSource pg,
    SqlOutboxTemplate sql,
    IPartitionManager partitionManager,
    TimeProvider timeProvider) : IConsumeLoader
{

    public async Task<LoadGroupResult> LoadGroup(
        OutboxMessageFilter filter,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1) return LoadGroupResult.Empty;


        try
        {
            var now = timeProvider.GetUtcNow();

            await partitionManager.EnsureParts(sql.DatabaseTaskTableName, now, [filter.TenantId, filter.ConsumerGroupId], cancellationToken);

            ConsumeTenantGroup pair = new(filter.ConsumerGroupId, filter.TenantId);

            await using var conn = await pg.OpenDbConnection(cancellationToken);

            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            await LockOffset(pair, conn, tx, cancellationToken);


            GroupOffset currentOffset = await GetOffset(pair, conn, tx, cancellationToken);

            var loadResult = await LoadGroupByOffset(currentOffset, filter, batchSize, now, conn, tx, cancellationToken);

            if (loadResult.GroupOffset.OffsetId != currentOffset.OffsetId)
            {
                await SaveOffset(pair, loadResult.GroupOffset, conn, tx, cancellationToken);
            }

            // COMMIT — unlock advisory lock!
            await tx.CommitAsync(cancellationToken);

            return loadResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LoadGroupResult.Empty;
        }
    }

    private async Task<LoadGroupResult> LoadGroupByOffset(
        GroupOffset currentOffset,
        OutboxMessageFilter filter,
        int batchSize,
        DateTimeOffset now,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlLoadConsumerGroup, conn, tx);

        command.Parameters.AddWithValue(CachedSqlParamNames.TenantId, filter.TenantId);
        command.Parameters.AddWithValue(CachedSqlParamNames.MsgPart, filter.Part);
        command.Parameters.AddWithValue(CachedSqlParamNames.ConsumerGroupId, filter.ConsumerGroupId);
        command.Parameters.AddWithValue(CachedSqlParamNames.GroupOffset, currentOffset.OffsetId);
        command.Parameters.AddWithValue(CachedSqlParamNames.Limit, batchSize);
        command.Parameters.AddWithValue(CachedSqlParamNames.NowDate, now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue(CachedSqlParamNames.FromDate, filter.FromDate.ToUnixTimeSeconds());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            int copiedRows = await reader.IsDBNullAsync(0, cancellationToken) ? 0 : reader.GetInt32(0);
            GroupOffset maxId = await reader.IsDBNullAsync(1, cancellationToken) ? GroupOffset.Empty : new(reader.GetString(1));

            return new(copiedRows, maxId);
        }

        return LoadGroupResult.Empty;
    }

    private async Task LockOffset(
        ConsumeTenantGroup pair,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        using var lockCmd = new NpgsqlCommand(sql.SqlLockOffset, conn, tx);
        int lockKey = HashText(pair);
        lockCmd.Parameters.AddWithValue(CachedSqlParamNames.OffsetKey, lockKey);
        await lockCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveOffset(
        ConsumeTenantGroup pair,
        GroupOffset newOffset,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        using var updateCmd = new NpgsqlCommand(sql.SqlUpdateOffset, conn, tx);
        updateCmd.Parameters.AddWithValue(CachedSqlParamNames.ConsumerGroupId, pair.ConsumerGroupId);
        updateCmd.Parameters.AddWithValue(CachedSqlParamNames.TenantId, pair.TenantId);
        updateCmd.Parameters.AddWithValue(CachedSqlParamNames.GroupOffset, newOffset.OffsetId);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<GroupOffset> GetOffset(
        ConsumeTenantGroup pair,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var selectCmd = new NpgsqlCommand(sql.SqlSelectOffset, conn, tx);

        selectCmd.Parameters.AddWithValue(CachedSqlParamNames.ConsumerGroupId, pair.ConsumerGroupId);
        selectCmd.Parameters.AddWithValue(CachedSqlParamNames.TenantId, pair.TenantId);

        object? currentOffsetObj = await selectCmd.ExecuteScalarAsync(cancellationToken);

        GroupOffset currentOffset = (currentOffsetObj == null)
            ? await InitializeOffset(pair, conn, tx, cancellationToken)
            : new GroupOffset((string)currentOffsetObj);

        return currentOffset;
    }

    private async Task<GroupOffset> InitializeOffset(
        ConsumeTenantGroup pair,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var initCmd = new NpgsqlCommand(sql.SqlInitOffset, conn, tx);

        initCmd.Parameters.AddWithValue(CachedSqlParamNames.ConsumerGroupId, pair.ConsumerGroupId);
        initCmd.Parameters.AddWithValue(CachedSqlParamNames.TenantId, pair.TenantId);
        await initCmd.ExecuteNonQueryAsync(cancellationToken);

        return GroupOffset.Empty;
    }


    public static int HashText(ConsumeTenantGroup pair)
    {
        uint hash = 0xffffffff;
        foreach (char c in pair.ToString())
        {
            hash ^= c;
            for (int i = 0; i < 8; i++)
            {
                hash = hash >> 1 ^ ((hash & 1) != 0 ? 0xA6BCD5B9u : 0);
            }
        }
        return (int)(hash ^ 0xffffffff);
    }
}
