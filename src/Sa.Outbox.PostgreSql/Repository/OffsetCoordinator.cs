using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Data.PostgreSql;
using System.Data;

namespace Sa.Outbox.PostgreSql.Repository;

internal sealed class OffsetCoordinator(
    IPgDataSource pg,
    SqlOutboxTemplate sql,
    ILogger? logger = null) : IOffsetCoordinator
{
    private bool _isInit = false;

    public async Task<GroupOffsetId> GetNextOffsetAndProcess(
        string groupId,
        int tenantId,
        Func<GroupOffsetId, CancellationToken, Task<GroupOffsetId>> process,
        CancellationToken cancellationToken = default)
    {

        int lockKey = HashText(groupId);

        await using var conn = await pg.OpenDbConnection(cancellationToken);
        try
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            // Получаем advisory lock — будет ждать, пока предыдущий не завершит
            await using var lockCmd = new NpgsqlCommand(sql.SqlLockOffset, conn, tx);
            lockCmd.Parameters.AddWithValue("@key", lockKey);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);

            if (!_isInit)
            {
                await CreateOffsetTableIfNotExists(conn, cancellationToken);
                _isInit = true;
            }

            // Читаем текущее смещение
            await using var selectCmd = new NpgsqlCommand(sql.SqlSelectOffset, conn, tx);

            selectCmd.Parameters.AddWithValue("@group_id", groupId);
            selectCmd.Parameters.AddWithValue("@tenant_id", tenantId);

            object? currentOffsetObj = await selectCmd.ExecuteScalarAsync(cancellationToken);

            GroupOffsetId currentOffset = (currentOffsetObj == null)
                ? await InitializeOffset(conn, tx, groupId, tenantId, cancellationToken)
                : new GroupOffsetId((string)currentOffsetObj);


            // Выполняем бизнес-логику (например, обработку сообщения)
            var newOffset = await process(currentOffset, cancellationToken);


            if (newOffset.OffsetId != currentOffset.OffsetId)
            {
                await using var updateCmd = new NpgsqlCommand(sql.SqlUpdateOffset, conn, tx);
                updateCmd.Parameters.AddWithValue("@group_id", groupId);
                updateCmd.Parameters.AddWithValue("@tenant_id", tenantId);
                updateCmd.Parameters.AddWithValue("@group_offset", newOffset.OffsetId);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // COMMIT — unlock advisory lock!
            await tx.CommitAsync(cancellationToken);

            return newOffset;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return GroupOffsetId.Empty;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Ошибка при обработке смещения для группы {GroupId}", groupId);
            throw;
        }
    }

    private async Task CreateOffsetTableIfNotExists(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql.SqlCreateOffsetTable, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<GroupOffsetId> InitializeOffset(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string groupId,
        int tenantId,
        CancellationToken cancellationToken)
    {
        await using var initCmd = new NpgsqlCommand(sql.SqlInitOffset, conn, tx);

        initCmd.Parameters.AddWithValue("@group_id", groupId);
        initCmd.Parameters.AddWithValue("@tenant_id", tenantId);
        await initCmd.ExecuteNonQueryAsync(cancellationToken);

        return GroupOffsetId.Empty;
    }


    private static int HashText(string str)
    {
        uint hash = 0xffffffff;
        foreach (char c in str)
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
