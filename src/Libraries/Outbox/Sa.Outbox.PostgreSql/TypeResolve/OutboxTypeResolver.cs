using Sa.Extensions;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.TypeResolve;

internal sealed class OutboxTypeResolver(
    IOutboxTypeCache cache,
    IOutboxMsgTypeRepository repository) : IOutboxTypeResolver
{
    private readonly SemaphoreSlim _insertLock = new(1, 1);

    public ValueTask<long> GetHashCode(string typeName, CancellationToken cancellationToken)
    {
        // Hot path: the snapshot is loaded and already knows this type. Returning a ValueTask
        // avoids the Task<long> the async method would allocate even when it completes
        // synchronously — this runs on every publish, poll and lock-renewal.
        if (cache.TryGetCode(typeName, out long cached))
        {
            return new ValueTask<long>(cached);
        }

        return new ValueTask<long>(GetHashCodeSlow(typeName, cancellationToken));
    }

    private async Task<long> GetHashCodeSlow(string typeName, CancellationToken cancellationToken)
    {
        long? code = await cache.GetCode(typeName, cancellationToken).ConfigureAwait(false);

        if (code is not null) return code.Value;

        long computed = typeName.GetMurmurHash3();

        await _insertLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock — another thread may have inserted it already.
            long? existing = await cache.GetCode(typeName, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return existing.Value;

            await repository.Insert(computed, typeName, cancellationToken).ConfigureAwait(false);
            await cache.Reset(cancellationToken).ConfigureAwait(false);

            // ON CONFLICT DO NOTHING does not tell us we won. Two short names can share a
            // Murmur3 value; returning the colliding code would make a consumer deserialize
            // the other type's payload. The hash input stays Type.Name — changing it would
            // split the cursor from rows already stored.
            string? stored = await cache.GetTypeName(computed, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(stored, typeName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Outbox message type '{typeName}' hashes to {computed}, which is already registered as '{stored ?? "<missing>"}'.");
            }
        }
        finally
        {
            _insertLock.Release();
        }

        return computed;
    }

    public ValueTask<string> GetTypeName(long typeHashCode, CancellationToken cancellationToken)
    {
        if (cache.TryGetTypeName(typeHashCode, out string? known) && known is not null)
        {
            return new ValueTask<string>(known);
        }

        return new ValueTask<string>(GetTypeNameSlow(typeHashCode, cancellationToken));
    }

    private async Task<string> GetTypeNameSlow(long typeHashCode, CancellationToken cancellationToken)
    {
        string? typeName = await cache.GetTypeName(typeHashCode, cancellationToken).ConfigureAwait(false);
        if (typeName != null) return typeName;

        await cache.Reset(cancellationToken).ConfigureAwait(false);

        return await cache.GetTypeName(typeHashCode, cancellationToken).ConfigureAwait(false) ?? typeHashCode.ToString();
    }
}
