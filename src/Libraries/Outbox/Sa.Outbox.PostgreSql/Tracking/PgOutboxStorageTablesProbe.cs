using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>One of the provider's lazily created tables.</summary>
/// <remarks>The order fixes the bit each table owns in <see cref="PgOutboxStorageTablesProbe"/>.</remarks>
internal enum PgOutboxStorageTable
{
    Message = 0,
    Task = 1,
    Delivery = 2,
    Error = 3,
    Offset = 4,
    Type = 5,
}

/// <summary>
/// Answers "does this table exist yet", remembers the answer, and re-runs a read whose answer turned
/// out to be stale.
/// </summary>
/// <remarks>
/// <para>
/// Most of the outbox is created lazily: the task table on the first consumer-group load, the delivery
/// and error tables on the first finished / failed delivery, the offset table on the first poll. A
/// diagnostic call against a fresh deployment therefore cannot assume any of them, and one catalog
/// lookup is cheaper than catching <c>42P01</c> from up to four separate statements.
/// </para>
/// <para>
/// The lookup used to run on <b>every</b> call, which cost each tracking call a second connection and
/// a second round trip before it read a single row. It is now remembered per table, and only the
/// "exists" half is remembered: a table this provider created stays created, so a warm process asks
/// for nothing; a table that is still missing is asked about again on the next call that needs it,
/// because "missing" is exactly the answer that goes stale the moment the first delivery lands.
/// The array passed to the catalog shrinks as tables are confirmed, and reaches zero on a deployment
/// that has seen a delivery.
/// </para>
/// <para>
/// A remembered "exists" can still go stale — nothing here watches for a table dropped behind our
/// back. That case surfaces as <c>42P01</c>/<c>3F000</c> from the statement itself: the cache is
/// dropped and the read is retried <b>once</b>, this time re-resolving which variant of the statement
/// to run against the corrected answer. A second failure is a real error and reaches the caller.
/// </para>
/// <para>
/// The remembered set and the discard generation share one <see cref="long"/> (bits 0-5 for the
/// tables, bits 32-63 for the generation) so a discard is a single compare-and-swap. That is what
/// stops a <see cref="LoadAsync"/> that read the catalog <i>before</i> a drop from putting the stale
/// "yes" back after the discard: it compares the generation it started under and reports nothing if
/// it moved.
/// </para>
/// </remarks>
internal sealed class PgOutboxStorageTablesProbe(
    IPgDataSource dataSource,
    SqlOutboxBuilder sql,
    PgOutboxTableSettings settings)
{
    private const int TableCount = 6;

    /// <summary>Lower 32 bits hold the confirmed tables; the upper 32 hold the discard generation.</summary>
    private const int GenerationShift = 32;
    private const long BitMask = (1L << TableCount) - 1;

    /// <summary>Confirmed tables and the discard generation, updated atomically as one value.</summary>
    private long _state;

    private readonly string[] _names =
    [
        settings.Message.TableName,
        settings.TaskQueue.TableName,
        settings.Delivery.TableName,
        settings.Error.TableName,
        settings.Offset.TableName,
        settings.Type.TableName,
    ];

    /// <summary>
    /// Reports whether <paramref name="table"/> exists right now, consulting the catalog only for the
    /// tables that have not been confirmed yet.
    /// </summary>
    public async ValueTask<bool> ExistsAsync(PgOutboxStorageTable table, CancellationToken cancellationToken)
    {
        int bit = 1 << (int)table;

        if ((BitsOf(Interlocked.Read(ref _state)) & bit) != 0) return true;

        return (BitsOf(await LoadAsync(cancellationToken).ConfigureAwait(false)) & bit) != 0;
    }

    /// <summary>
    /// Runs <paramref name="read"/> against a table set that is known to be true, re-running it once
    /// if a statement reports that a relation is missing after all.
    /// </summary>
    public async Task<T> ReadAsync<T>(
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            uint generation = GenerationOf(Interlocked.Read(ref _state));

            try
            {
                return await read(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (attempt == 0 && IsMissingRelation(ex))
            {
                // We said a table existed and it did not: forget every confirmation and let the
                // read resolve its statement against the truth. One retry only — repeating a
                // statement that fails twice would hide a genuine error behind a loop.
                Discard(generation);
            }
        }
    }

    /// <summary>
    /// Asks the catalog about the tables that have not been confirmed yet and folds the answer into
    /// the remembered set.
    /// </summary>
    /// <returns>
    /// The confirmed tables, or 0 when the set was discarded while the catalog query was in flight —
    /// an answer computed under a generation that no longer applies describes nothing usable, so it
    /// is reported as "no table exists" and never remembered.
    /// </returns>
    private async ValueTask<long> LoadAsync(CancellationToken cancellationToken)
    {
        long known = Interlocked.Read(ref _state);
        uint generation = GenerationOf(known);
        long confirmed = BitsOf(known);

        List<string> unknown = new(TableCount);

        for (int i = 0; i < TableCount; i++)
        {
            if ((confirmed & (1 << i)) == 0) unknown.Add(_names[i]);
        }

        if (unknown.Count == 0) return confirmed;

        await using NpgsqlConnection connection = await dataSource
            .OpenDbConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql.SqlTrackExistingTables, connection);
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.TrackSchema, settings.DatabaseSchemaName));
        command.Parameters.Add(new NpgsqlParameter<string[]>(SqlParam.TrackTables, [.. unknown]));

        long found = 0;

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int index = Array.IndexOf(_names, reader.GetString(0));

            if (index >= 0) found |= 1L << index;
        }

        return Commit(generation, found);
    }

    /// <summary>
    /// Folds a catalog answer into the remembered set, unless the set was discarded meanwhile.
    /// </summary>
    /// <remarks>
    /// The merge is a compare-and-swap rather than <c>Interlocked.Or</c> so a concurrent call's
    /// confirmation is kept, and the generation check is what keeps a pre-drop answer out: the
    /// returned value is the merged set (bits only), or 0 when the answer is stale. Using the
    /// pre-merge set would have handed the caller "no table exists" on the very first call.
    /// </remarks>
    private long Commit(uint generation, long found)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _state);

            if (GenerationOf(current) != generation) return 0;

            long merged = Pack(generation, BitsOf(current) | found);

            if (Interlocked.CompareExchange(ref _state, merged, current) == current) return BitsOf(merged);
        }
    }

    /// <summary>
    /// Forgets every confirmation so the next lookup asks the catalog again.
    /// </summary>
    /// <param name="generation">The generation the failed read started under.</param>
    /// <remarks>
    /// A no-op when the generation already moved: another call discarded the set after this one
    /// started, so the bits this call would clear are gone and bumping again would only invalidate
    /// that call's fresh answer.
    /// </remarks>
    private void Discard(uint generation)
    {
        long discarded = Pack(unchecked(generation + 1), 0);

        while (true)
        {
            long current = Interlocked.Read(ref _state);

            if (GenerationOf(current) != generation) return;

            if (Interlocked.CompareExchange(ref _state, discarded, current) == current) return;
        }
    }

    private static uint GenerationOf(long state) => (uint)(state >>> GenerationShift);

    private static long BitsOf(long state) => state & BitMask;

    private static long Pack(uint generation, long bits)
        => ((long)generation << GenerationShift) | (bits & BitMask);

    /// <summary>
    /// Whether the failure means "this relation (or schema) is not there", rather than anything else
    /// the statement could have failed on.
    /// </summary>
    private static bool IsMissingRelation(PostgresException ex)
        => ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName;
}
