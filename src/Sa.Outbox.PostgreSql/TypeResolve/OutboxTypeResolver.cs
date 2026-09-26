using Sa.Extensions;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.TypeResolve;

internal sealed class OutboxTypeResolver(
    IOutboxTypeCache cache,
    IOutboxMsgTypeRepository repository) : IOutboxTypeResolver
{
    private readonly SemaphoreSlim _insertLock = new(1, 1);

    public async Task<long> GetHashCode(string typeName, CancellationToken cancellationToken)
    {

        long code = await cache.GetCode(typeName, cancellationToken);

        if (code != 0) return code;

        code = typeName.GetMurmurHash3();

        await _insertLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring the lock — another thread may have inserted it already.
            long existing = await cache.GetCode(typeName, cancellationToken);
            if (existing != 0) return existing;

            await repository.Insert(code, typeName, cancellationToken);
            await cache.Reset(cancellationToken);
        }
        finally
        {
            _insertLock.Release();
        }

        return code;
    }

    public async Task<string> GetTypeName(long typeHashCode, CancellationToken cancellationToken)
    {
        string? typeName = await cache.GetTypeName(typeHashCode, cancellationToken);
        if (typeName != null) return typeName;

        await cache.Reset(cancellationToken);

        return await cache.GetTypeName(typeHashCode, cancellationToken) ?? typeHashCode.ToString();
    }
}
