using Sa.Outbox.Delivery;
using Sa.Outbox.Exceptions;

namespace Sa.Outbox.InMemory;

/// <summary>
/// The whole storage of the in-memory provider: an id map plus a per-group ordered index.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="Dictionary{TKey,TValue}"/> holds every record by id; one
/// <see cref="SortedSet{T}"/> per <c>(tenant, part, payload type)</c> holds the ids in publish order.
/// That is the entire data model — there is no schema, no serialization format and no migration,
/// which is what keeps the reference implementation readable.
/// </para>
/// <para>
/// All state changes happen under one lock. Publishing is O(1) per message, renting is O(k) for
/// <c>k</c> rented messages (the index is already ordered), and lease extension walks only the ids
/// owned by the calling transaction. Timestamps come from <see cref="TimeProvider"/>, never from
/// <see cref="DateTimeOffset.UtcNow"/>, so tests can drive the clock.
/// </para>
/// </remarks>
internal sealed class InMemoryOutboxStore(TimeProvider timeProvider, InMemoryOutboxSettings settings)
{
    private readonly object _gate = new();
    private readonly Dictionary<long, InMemoryOutboxRecord> _byId = [];
    private readonly Dictionary<GroupKey, SortedSet<long>> _index = [];
    private readonly Dictionary<string, HashSet<long>> _leases = new(StringComparer.Ordinal);

    private long _nextId;

    /// <summary>The number of messages currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _byId.Count;
        }
    }

    /// <summary>
    /// Stores a publish batch and returns the number of messages saved.
    /// </summary>
    /// <exception cref="OutboxException">The batch would exceed <see cref="InMemoryOutboxSettings.Capacity"/>.</exception>
    public ulong Insert(IReadOnlyList<InMemoryOutboxRecord> records)
    {
        if (records.Count == 0)
            return 0;

        lock (_gate)
        {
            if (settings.Capacity > 0 && _byId.Count + records.Count > settings.Capacity)
            {
                throw new OutboxException(
                    $"The in-memory Outbox capacity of {settings.Capacity} messages is exhausted " +
                    $"(held {_byId.Count}, publishing {records.Count}). Raise InMemoryOutboxSettings.Capacity " +
                    "or let the consumer group drain the store.");
            }

            foreach (InMemoryOutboxRecord record in records)
            {
                record.Id = ++_nextId;

                _byId.Add(record.Id, record);

                GroupKey key = new(record.TenantId, record.Part, record.PayloadType);

                if (!_index.TryGetValue(key, out SortedSet<long>? ids))
                {
                    ids = [];
                    _index.Add(key, ids);
                }

                ids.Add(record.Id);
            }

            return (ulong)records.Count;
        }
    }

    /// <summary>
    /// Leases up to <paramref name="max"/> rentable messages of the filter's group, in publish order.
    /// </summary>
    public List<InMemoryOutboxRecord> Rent(TimeSpan lockDuration, OutboxMessageFilter filter, int max)
    {
        if (max <= 0)
            return [];

        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!_index.TryGetValue(new GroupKey(filter.TenantId, filter.Part, filter.PayloadType), out SortedSet<long>? ids))
                return [];

            List<InMemoryOutboxRecord> rented = new(Math.Min(max, ids.Count));

            foreach (long id in ids)
            {
                if (rented.Count == max)
                    break;

                InMemoryOutboxRecord record = _byId[id];

                if (!IsRentable(record, now))
                    continue;

                if (record.CreatedAt < filter.FromDate || record.CreatedAt > filter.ToDate)
                    continue;

                // The previous owner, if any, lost the lease: drop the id from its renewal set so a
                // stale ExtendDelivery cannot walk a message that is no longer its own.
                if (record.TransactId is { } previous
                    && !string.Equals(previous, filter.TransactId, StringComparison.Ordinal)
                    && _leases.TryGetValue(previous, out HashSet<long>? stale))
                {
                    stale.Remove(record.Id);

                    if (stale.Count == 0)
                        _leases.Remove(previous);
                }

                record.Status = DeliveryStatusCode.Processing;
                record.LeaseExpiresAt = now + lockDuration;
                record.TransactId = filter.TransactId;
                record.RentGeneration++;

                if (!_leases.TryGetValue(filter.TransactId, out HashSet<long>? owned))
                {
                    owned = [];
                    _leases.Add(filter.TransactId, owned);
                }

                owned.Add(record.Id);
                rented.Add(record);
            }

            return rented;
        }
    }

    /// <summary>
    /// Applies the outcome of a rented batch and returns the number of outcomes actually applied.
    /// </summary>
    /// <remarks>
    /// An outcome whose rent generation no longer matches the record — the lease expired and another
    /// call re-rented the message — is dropped. Delivery is at-least-once, so the message stays with
    /// its new owner instead of being settled by a stale worker.
    /// </remarks>
    public int Return(IReadOnlyList<InMemoryOutboxOutcome> outcomes, OutboxMessageFilter filter)
    {
        if (outcomes.Count == 0)
            return 0;

        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            int applied = 0;

            foreach (InMemoryOutboxOutcome outcome in outcomes)
            {
                if (!_byId.TryGetValue(outcome.Id, out InMemoryOutboxRecord? record))
                    continue;

                if (record.RentGeneration != outcome.Generation
                    || !string.Equals(record.TransactId, filter.TransactId, StringComparison.Ordinal))
                {
                    continue;
                }

                applied++;

                record.Status = outcome.Status.Code;
                record.StatusMessage = outcome.Status.Message;
                record.LeaseExpiresAt = null;
                record.TransactId = null;

                if (record.Status is DeliveryStatusCode.Retry or DeliveryStatusCode.Warn)
                    record.Attempt++;

                record.AvailableAt = record.Status is DeliveryStatusCode.Postpone
                    or DeliveryStatusCode.Retry
                    or DeliveryStatusCode.Warn
                        ? now + (outcome.Delay > TimeSpan.Zero ? outcome.Delay : TimeSpan.Zero)
                        : null;
            }

            Release(filter.TransactId);

            return applied;
        }
    }

    /// <summary>
    /// Extends the leases owned by the filter's transaction and returns how many were extended.
    /// </summary>
    /// <remarks>
    /// The return value is part of the contract: <c>0</c> tells the pipeline the lease is gone, so it
    /// stops the batch. A lease that already expired is not extended — the message may have been
    /// re-rented by another call, and pretending otherwise would hide a split brain.
    /// </remarks>
    public int Extend(TimeSpan lockExpiration, OutboxMessageFilter filter)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!_leases.TryGetValue(filter.TransactId, out HashSet<long>? owned))
                return 0;

            int extended = 0;

            foreach (long id in owned)
            {
                InMemoryOutboxRecord record = _byId[id];

                if (record.Status != DeliveryStatusCode.Processing)
                    continue;

                if (!string.Equals(record.TransactId, filter.TransactId, StringComparison.Ordinal))
                    continue;

                if (record.LeaseExpiresAt is null || record.LeaseExpiresAt <= now)
                    continue;

                record.LeaseExpiresAt = now + lockExpiration;
                extended++;
            }

            return extended;
        }
    }

    /// <summary>The distinct tenants that still hold deliverable messages.</summary>
    public int[] GetTenantIds()
    {
        lock (_gate)
        {
            SortedSet<int> tenants = [];

            foreach (InMemoryOutboxRecord record in _byId.Values)
            {
                if (IsDeliverable(record.Status))
                    tenants.Add(record.TenantId);
            }

            return [.. tenants];
        }
    }

    private void Release(string transactId)
    {
        if (!_leases.TryGetValue(transactId, out HashSet<long>? owned))
            return;

        owned.RemoveWhere(id => !_byId.TryGetValue(id, out InMemoryOutboxRecord? record)
            || record.TransactId is null
            || !string.Equals(record.TransactId, transactId, StringComparison.Ordinal));

        if (owned.Count == 0)
            _leases.Remove(transactId);
    }

    private static bool IsRentable(InMemoryOutboxRecord record, DateTimeOffset now)
    {
        if (record.AvailableAt is { } available && available > now)
            return false;

        return record.Status switch
        {
            DeliveryStatusCode.Pending => true,
            // An expired lease is rentable again: the previous worker either finished silently or
            // died. At-least-once means the consumer must be idempotent.
            DeliveryStatusCode.Processing => record.LeaseExpiresAt is null || record.LeaseExpiresAt <= now,
            DeliveryStatusCode.Postpone or DeliveryStatusCode.Retry or DeliveryStatusCode.Warn =>
                record.LeaseExpiresAt is null || record.LeaseExpiresAt <= now,
            _ => false,
        };
    }

    private static bool IsDeliverable(DeliveryStatusCode status)
        => status is DeliveryStatusCode.Pending
            or DeliveryStatusCode.Processing
            or DeliveryStatusCode.Postpone
            or DeliveryStatusCode.Retry
            or DeliveryStatusCode.Warn;

    private readonly record struct GroupKey(int TenantId, string Part, string PayloadType);
}

/// <summary>
/// The outcome of one rented message, as <see cref="InMemoryOutboxStore.Return"/> applies it.
/// </summary>
/// <param name="Id">The record id (the first half of the rent token).</param>
/// <param name="Generation">The rent generation the outcome belongs to.</param>
/// <param name="Status">The consumer's delivery status.</param>
/// <param name="Delay">How long the message stays unrentable for a delayed outcome.</param>
internal readonly record struct InMemoryOutboxOutcome(
    long Id,
    long Generation,
    DeliveryStatus Status,
    TimeSpan Delay);
