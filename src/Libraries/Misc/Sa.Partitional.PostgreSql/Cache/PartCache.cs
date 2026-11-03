using Sa.Classes;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.SqlBuilder;
using System.Collections.Concurrent;

namespace Sa.Partitional.PostgreSql.Cache;

internal sealed class PartCache(
    IPartRepository repository
    , ISqlBuilder sqlBuilder
    , PartCacheSettings settings
    , TimeProvider? timeProvider = null) : IPartCache
{
    /// <summary>
    /// One cached partition-metadata snapshot for a single table.
    /// </summary>
    /// <param name="Load">
    /// The in-flight or completed database read. Wrapped in <see cref="Lazy{T}"/> so that
    /// concurrent callers share a single round-trip (single-flight), and so the terminal state can
    /// be inspected via <see cref="Lazy{T}.IsValueCreated"/> without forcing the load to start.
    /// </param>
    /// <param name="ExpiresAt">
    /// Lazy TTL deadline, evaluated against the <see cref="System.TimeProvider"/> on the read path.
    /// Deliberately not a timer, so the cache owns no timer and no disposable resource.
    /// </param>
    private sealed record CacheEntry(
        Lazy<Task<List<PartByRangeInfo>>> Load
        , DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> InCache(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
    {
        // Resolve the builder once: this path runs for every EnsureParts call.
        ISqlTableBuilder? builder = sqlBuilder[tableName];
        if (builder is null) return false;

        List<PartByRangeInfo> list = await GetPartsInCache(builder, tableName, cancellationToken).ConfigureAwait(false);

        if (list.Count == 0) return false;

        // A plain loop, not List.Exists with a capturing lambda: the closure and the
        // SequenceEqual enumerator were allocated per EnsureParts even on a cache hit.
        foreach (PartByRangeInfo cached in list)
        {
            if (cached.PartValues.Length == partValues.Length
                && cached.PartValues.AsSpan().SequenceEqual(partValues)
                && cached.PartBy.GetRange(cached.FromDate).InRange(date))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Cache key of a table: the resolved fully qualified name when the builder knows the table, so
    /// <c>events</c> and <c>public.events</c> share one snapshot instead of loading the same table
    /// twice under two keys.
    /// </summary>
    private static string CacheKey(string tableName, ISqlTableBuilder? builder)
        => builder?.FullName ?? tableName;

    private async Task<List<PartByRangeInfo>> GetPartsInCache(
        ISqlTableBuilder builder,
        string tableName,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        string key = CacheKey(tableName, builder);

        if (_cache.TryGetValue(key, out CacheEntry? entry) && IsUsable(entry, now))
        {
            return await AwaitLoad(entry, key, cancellationToken).ConfigureAwait(false);
        }

        // Expired, faulted or cancelled: drop it so the next read starts from a clean slate.
        if (_cache.TryRemove(key, out CacheEntry? stale)) Discard(stale);

        CacheEntry fresh = new(
            new Lazy<Task<List<PartByRangeInfo>>>(
                () => SelectPartsInDb(builder, tableName),
                LazyThreadSafetyMode.ExecutionAndPublication),
            now + settings.CacheTtl);

        // Losers of the race adopt the winner's entry, so every caller awaits one single load.
        entry = _cache.GetOrAdd(key, fresh);

        return await AwaitLoad(entry, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches an entry that is no longer cached, making sure a load that is still in flight stays
    /// observed: once the entry is gone, the only thing that could still read its failure is a
    /// continuation attached here. Without it a load that faults after every caller left (each one
    /// through <c>WaitAsync(token)</c>) becomes an unobserved task, and the database error behind it
    /// is reported nowhere.
    /// </summary>
    private static void Discard(CacheEntry? entry)
    {
        // IsValueCreated first: touching .Value on a not-yet-started Lazy would start the load.
        if (entry is null || !entry.Load.IsValueCreated) return;

        _ = entry.Load.Value.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// An entry is reusable while it has not expired and its load has neither faulted nor been
    /// cancelled. A load that is still in flight counts as reusable — sharing it is the whole point.
    /// </summary>
    private static bool IsUsable(CacheEntry entry, DateTimeOffset now)
    {
        if (entry.ExpiresAt <= now) return false;

        // IsValueCreated is checked first on purpose: touching .Value on a not-yet-started
        // Lazy<Task<T>> would start the load as a side effect of a mere status check.
        if (!entry.Load.IsValueCreated) return true;

        Task<List<PartByRangeInfo>> load = entry.Load.Value;
        return !load.IsFaulted && !load.IsCanceled;
    }

    private async Task<List<PartByRangeInfo>> AwaitLoad(
        CacheEntry entry,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        Task<List<PartByRangeInfo>> load = entry.Load.Value;

        // Cache hit: the shared load has already completed. Returning the list directly avoids the
        // Task that WaitAsync allocates for an already-finished operation. A cancelled caller still
        // goes through WaitAsync below, preserving the token contract.
        if (load.IsCompletedSuccessfully && !cancellationToken.IsCancellationRequested)
        {
            return load.Result;
        }

        try
        {
            // WaitAsync rather than a bare await: the caller's token must cancel only this await and
            // never the shared load that other callers are already waiting on. Handing the caller's
            // token to the cached task instead would let one caller poison the entry for all.
            return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (load.IsFaulted || load.IsCanceled)
        {
            // Evict — but only while this exact entry is still the current one (TryRemove matches
            // key AND value), so a concurrent reload is not thrown away. The original exception
            // reaches the caller, and the next call re-queries the database instead of replaying a
            // stale failure forever.
            //
            // It has to be the *cache key*, not the caller's table name: the entry was stored under
            // the resolved full name (see CacheKey), so removing by a bare name here would quietly
            // stop evicting for a caller that passes "events" instead of "public.events".
            if (_cache.TryRemove(new KeyValuePair<string, CacheEntry>(cacheKey, entry))) Discard(entry);
            throw;
        }
    }

    // search and set the cache duration based result set
    private async Task<List<PartByRangeInfo>> SelectPartsInDb(ISqlTableBuilder builder, string tableName)
    {
        try
        {
            DateTimeOffset from = SnapshotFloor(builder);

            // CancellationToken.None on purpose: this load is shared by every caller awaiting the
            // entry, so it must not be bound to any single caller's token. Npgsql's command and
            // connection timeouts are what bound it.
            return await repository.GetPartsFromDate(tableName, from, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException ex) when (PgErrorCodes.IsUndefinedTable(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Lower bound of the snapshot loaded for a table.
    /// </summary>
    /// <remarks>
    /// The snapshot query keeps partitions whose range <i>starts</i> at or after this instant. The
    /// configured <see cref="PartCacheSettings.CachedFromDate"/> alone is not enough: it defaults to
    /// one day, while the partition currently being written to may have started much earlier — a
    /// monthly partition started on the 1st, a yearly one on 1 January. Such a partition would never
    /// be in the snapshot, so every <c>EnsureParts</c> for it looked like a miss and re-ran the whole
    /// partition DDL. The window is therefore widened to at least one full partition period of the
    /// table; a daily period equals the default <see cref="PartCacheSettings.CachedFromDate"/>, so
    /// nothing changes for day-partitioned tables.
    /// </remarks>
    private DateTimeOffset SnapshotFloor(ISqlTableBuilder builder)
    {
        TimeSpan lookback = settings.CachedFromDate;

        if (builder.PartBy is { } partBy)
        {
            TimeSpan period = partBy.PartByRange switch
            {
                PartByRange.Day => TimeSpan.FromDays(1),
                PartByRange.Month => TimeSpan.FromDays(31),
                PartByRange.Year => TimeSpan.FromDays(366),
                _ => TimeSpan.FromDays(1),
            };

            if (period > lookback) lookback = period;
        }

        return (_timeProvider.GetUtcNow() - lookback).StartOfDay();
    }

    public async Task EnsureCache(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
    {
        if (await InCache(tableName, date, partValues, cancellationToken).ConfigureAwait(false)) return;

        await repository.CreatePart(tableName, date, partValues, cancellationToken).ConfigureAwait(false);

        await RemoveCache(tableName, cancellationToken).ConfigureAwait(false);

        // The DDL just executed is `CREATE TABLE IF NOT EXISTS` for the leaf plus the cache row, so
        // when it returns the partition exists — re-reading the snapshot could only tell us whether
        // the *cache window* covers the date. It does not for a date older than
        // `PartCacheSettings.CachedFromDate`: the old second `InCache` therefore returned false for
        // every backdated write, driving a full DDL pass and a cache flush on each call, forever.
    }

    public Task RemoveCache(string tableName, CancellationToken cancellationToken = default)
    {
        ISqlTableBuilder? builder = sqlBuilder[tableName];
        if (_cache.TryRemove(CacheKey(tableName, builder), out CacheEntry? removed)) Discard(removed);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops every cached snapshot. Used after a bulk change — a migration that created partitions,
    /// or a cleanup that dropped them — which happened behind the cache's back.
    /// </summary>
    public Task RemoveCache()
    {
        foreach (KeyValuePair<string, CacheEntry> item in _cache)
        {
            if (_cache.TryRemove(item)) Discard(item.Value);
        }

        return Task.CompletedTask;
    }
}
