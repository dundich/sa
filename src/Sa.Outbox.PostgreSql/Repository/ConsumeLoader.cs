using System.Data;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class ConsumeLoader(
    IPgDataSource pg,
    SqlOutboxTemplate sql,
    IPartitionManager partitionManager) : IConsumeLoader
{

    public async Task<LoadGroupResult> LoadGroup(
        OutboxMessageFilter filter,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1) return LoadGroupResult.Empty;


        try
        {

            await partitionManager.EnsureParts(sql.DatabaseTaskTableName, filter.NowDate, [filter.TenantId, filter.ConsumerGroupId], cancellationToken);

            ConsumeTenantGroup pair = new(filter.ConsumerGroupId, filter.TenantId);

            await using var conn = await pg.OpenDbConnection(cancellationToken);

            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            await LockOffset(pair, conn, tx, cancellationToken);


            GroupOffset currentOffset = await GetOffset(pair, conn, tx, cancellationToken);

            var loadResult = await LoadGroupByOffset(currentOffset, filter, batchSize, conn, tx, cancellationToken);

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
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlLoadConsumerGroup, conn, tx);

        command.Parameters.AddWithValue(SqlParam.TenantId, filter.TenantId);
        command.Parameters.AddWithValue(SqlParam.MsgPart, filter.Part);
        command.Parameters.AddWithValue(SqlParam.ConsumerGroupId, filter.ConsumerGroupId);
        command.Parameters.AddWithValue(SqlParam.GroupOffset, currentOffset.OffsetId);
        command.Parameters.AddWithValue(SqlParam.Limit, batchSize);
        command.Parameters.AddWithValue(SqlParam.NowDate, filter.NowDate.ToUnixTimeSeconds());

        command.Parameters.AddWithValue(SqlParam.FromDate, filter.FromDate.ToUnixTimeSeconds());
        command.Parameters.AddWithValue(SqlParam.ToDate, filter.ToDate.ToUnixTimeSeconds());

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
        lockCmd.Parameters.AddWithValue(SqlParam.OffsetKey, lockKey);
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
        updateCmd.Parameters.AddWithValue(SqlParam.ConsumerGroupId, pair.ConsumerGroupId);
        updateCmd.Parameters.AddWithValue(SqlParam.TenantId, pair.TenantId);
        updateCmd.Parameters.AddWithValue(SqlParam.GroupOffset, newOffset.OffsetId);
        await updateCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<GroupOffset> GetOffset(
        ConsumeTenantGroup pair,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var selectCmd = new NpgsqlCommand(sql.SqlSelectOffset, conn, tx);

        selectCmd.Parameters.AddWithValue(SqlParam.ConsumerGroupId, pair.ConsumerGroupId);
        selectCmd.Parameters.AddWithValue(SqlParam.TenantId, pair.TenantId);

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

        initCmd.Parameters.AddWithValue(SqlParam.ConsumerGroupId, pair.ConsumerGroupId);
        initCmd.Parameters.AddWithValue(SqlParam.TenantId, pair.TenantId);
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
