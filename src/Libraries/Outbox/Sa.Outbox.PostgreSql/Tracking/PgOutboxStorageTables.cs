using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>
/// Which of the provider's tables exist in the database right now.
/// </summary>
/// <remarks>
/// Most of the outbox is created lazily: the task table on the first consumer-group load, the delivery
/// and error tables on the first finished / failed delivery, the offset table on the first poll. A
/// diagnostic call against a fresh deployment therefore cannot assume any of them, and one catalog
/// lookup is cheaper than catching <c>42P01</c> from up to four separate statements (which would also
/// poison a caller-supplied transaction, if one were ever accepted).
/// </remarks>
internal readonly record struct PgOutboxStorageTables(
    bool Message,
    bool Task,
    bool Delivery,
    bool Error,
    bool Offset,
    bool Type)
{
    public static async Task<PgOutboxStorageTables> LoadAsync(
        IPgDataSource dataSource,
        SqlOutboxBuilder sql,
        PgOutboxTableSettings settings,
        CancellationToken cancellationToken)
    {
        string[] names =
        [
            settings.Message.TableName,
            settings.TaskQueue.TableName,
            settings.Delivery.TableName,
            settings.Error.TableName,
            settings.Offset.TableName,
            settings.Type.TableName,
        ];

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql.SqlTrackExistingTables, connection);
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.TrackSchema, settings.DatabaseSchemaName));
        command.Parameters.Add(new NpgsqlParameter<string[]>(SqlParam.TrackTables, names));

        HashSet<string> existing = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            existing.Add(reader.GetString(0));
        }

        return new PgOutboxStorageTables(
            Message: existing.Contains(settings.Message.TableName),
            Task: existing.Contains(settings.TaskQueue.TableName),
            Delivery: existing.Contains(settings.Delivery.TableName),
            Error: existing.Contains(settings.Error.TableName),
            Offset: existing.Contains(settings.Offset.TableName),
            Type: existing.Contains(settings.Type.TableName));
    }
}
