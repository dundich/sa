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

            if (!loadResult.GroupOffset.IsEmpty() && loadResult.GroupOffset.OffsetId != currentOffset.OffsetId)
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


        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, filter.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.MsgPart, filter.Part));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, filter.ConsumerGroupId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.GroupOffset, currentOffset.OffsetId));
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.Limit, batchSize));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.NowDate, filter.NowDate.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.FromDate, filter.FromDate.ToUnixTimeSeconds()));
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.ToDate, filter.ToDate.ToUnixTimeSeconds()));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            int copiedRows = await reader.IsDBNullAsync(0, cancellationToken) ? 0 : reader.GetInt32(0);

            if (copiedRows == 0) return LoadGroupResult.Empty;

            GroupOffset maxId = await reader.IsDBNullAsync(1, cancellationToken) ? GroupOffset.Empty : new(reader.GetString(1));

            return new LoadGroupResult(copiedRows, maxId);
        }
        else
        {
            return LoadGroupResult.Empty;
        }
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
        using var command = new NpgsqlCommand(sql.SqlUpdateOffset, conn, tx);

        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, pair.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, pair.ConsumerGroupId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.GroupOffset, newOffset.OffsetId));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<GroupOffset> GetOffset(
        ConsumeTenantGroup pair,
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql.SqlSelectOffset, conn, tx);

        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, pair.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, pair.ConsumerGroupId));

        object? currentOffsetObj = await command.ExecuteScalarAsync(cancellationToken);

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
        await using var command = new NpgsqlCommand(sql.SqlInitOffset, conn, tx);

        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, pair.TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, pair.ConsumerGroupId));

        await command.ExecuteNonQueryAsync(cancellationToken);

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
