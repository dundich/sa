using Sa.Classes;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.Classes;
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
        if (sqlBuilder[tableName] == null) return false;

        List<PartByRangeInfo> list = await GetPartsInCache(tableName, cancellationToken).ConfigureAwait(false);

        if (list.Count == 0) return false;

        return list.Exists(c => partValues.SequenceEqual(c.PartValues) && c.PartBy.GetRange(c.FromDate).InRange(date));
    }

    private async Task<List<PartByRangeInfo>> GetPartsInCache(string tableName, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(tableName, out CacheEntry? entry) && IsUsable(entry, now))
        {
            return await AwaitLoad(entry, tableName, cancellationToken).ConfigureAwait(false);
        }

        // Expired, faulted or cancelled: drop it so the next read starts from a clean slate.
        if (_cache.TryRemove(tableName, out CacheEntry? stale)) Discard(stale);

        CacheEntry fresh = new(
            new Lazy<Task<List<PartByRangeInfo>>>(
                () => SelectPartsInDb(tableName),
                LazyThreadSafetyMode.ExecutionAndPublication),
            now + settings.CacheTtl);

        // Losers of the race adopt the winner's entry, so every caller awaits one single load.
        entry = _cache.GetOrAdd(tableName, fresh);

        return await AwaitLoad(entry, tableName, cancellationToken).ConfigureAwait(false);
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
        string tableName,
        CancellationToken cancellationToken)
    {
        Task<List<PartByRangeInfo>> load = entry.Load.Value;

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
            if (_cache.TryRemove(new KeyValuePair<string, CacheEntry>(tableName, entry))) Discard(entry);
            throw;
        }
    }

    // search and set the cache duration based result set
    private async Task<List<PartByRangeInfo>> SelectPartsInDb(string tableName)
    {
        try
        {
            DateTimeOffset from = (_timeProvider.GetUtcNow() - settings.CachedFromDate).StartOfDay();

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

    public async Task<bool> EnsureCache(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default)
    {
        bool result = await InCache(tableName, date, partValues, cancellationToken).ConfigureAwait(false);
        if (result) return true;

        await repository.CreatePart(tableName, date, partValues, cancellationToken).ConfigureAwait(false);

        await RemoveCache(tableName, cancellationToken).ConfigureAwait(false);

        result = await InCache(tableName, date, partValues, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public Task RemoveCache(string tableName, CancellationToken cancellationToken = default)
    {
        if (_cache.TryRemove(tableName, out CacheEntry? removed)) Discard(removed);
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
