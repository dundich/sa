using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Data.PostgreSql;
using System.Data;

namespace Sa.Outbox.PostgreSql.Repository;

internal sealed class OffsetCoordinator(
    IPgDataSource pg,
    PgOutboxTableSettings tableSettings,
    ILogger? logger = null) : IOffsetCoordinator
{
    public async Task<GroupOffsetId> GetNextOffsetAndProcess(
        string groupId,
        Func<GroupOffsetId, CancellationToken, Task<GroupOffsetId>> process,
        CancellationToken cancellationToken = default)
    {

        int lockKey = HashText(groupId);

        await using var conn = await pg.OpenDbConnection(cancellationToken);
        await conn.OpenAsync(cancellationToken);

        try
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            // Получаем advisory lock — будет ждать, пока предыдущий не завершит
            await using var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", conn, tx);
            lockCmd.Parameters.AddWithValue("@key", lockKey);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);

            // Теперь мы — единственный, кто работает в этой группе

            // Читаем текущее смещение
            await using var selectCmd = new NpgsqlCommand($"""
SELECT group_offset FROM {tableSettings.GetQualifiedOffsetTableName} 
WHERE group_id = @group_id FOR UPDATE;
""", conn, tx);

            selectCmd.Parameters.AddWithValue("@group_id", groupId);

            var currentOffsetObj = await selectCmd.ExecuteScalarAsync(cancellationToken);

            GroupOffsetId currentOffset = (currentOffsetObj == null)
                ? await InitializeOffset(conn, tx, groupId, cancellationToken)
                : new GroupOffsetId((string)currentOffsetObj);


            // Выполняем бизнес-логику (например, обработку сообщения)
            var newOffset = await process(currentOffset, cancellationToken);

            // Обновляем смещение
            await using var updateCmd = new NpgsqlCommand(@$"
UPDATE {tableSettings.GetQualifiedOffsetTableName}  
SET group_offset = @group_offset, group_updated_at = NOW() 
WHERE group_id = @group_id;", conn, tx);

            updateCmd.Parameters.AddWithValue("@group_id", groupId);
            updateCmd.Parameters.AddWithValue("@group_offset", newOffset.OffsetId);

            await updateCmd.ExecuteNonQueryAsync(cancellationToken);

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

    private async Task<GroupOffsetId> InitializeOffset(NpgsqlConnection conn, NpgsqlTransaction tx, string groupId, CancellationToken ct)
    {
        await using var initCmd = new NpgsqlCommand(@$"
            INSERT INTO {tableSettings.GetQualifiedOffsetTableName} (group_id, group_offset)
            VALUES (@group_id, '01KBQ8DYRBSQ11R20ZKRBYD2G9')
            ON CONFLICT (group_id) DO NOTHING;", conn, tx);

        initCmd.Parameters.AddWithValue("@group_id", groupId);
        await initCmd.ExecuteNonQueryAsync(ct);

        return new GroupOffsetId("01KBQ8DYRBSQ11R20ZKRBYD2G9");
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
