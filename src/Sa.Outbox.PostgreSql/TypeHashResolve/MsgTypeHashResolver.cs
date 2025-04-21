using Sa.Extensions;
using Sa.Outbox.PostgreSql.Repository;

namespace Sa.Outbox.PostgreSql.TypeHashResolve;


internal class MsgTypeHashResolver(IMsgTypeCache cache, IMsgTypeRepository repository) : IMsgTypeHashResolver
{
    private int _triggered = 0;

    public async Task<long> GetCode(string typeName, CancellationToken cancellationToken)
    {

        long code = await cache.GetCode(typeName, cancellationToken);

        if (code != 0) return code;

        code = typeName.GetMurmurHash3();

        if (Interlocked.CompareExchange(ref _triggered, 1, 0) == 1) return code;

        try
        {
            await repository.Insert(code, typeName, cancellationToken);
            await cache.Reset(cancellationToken);
        }
        finally
        {
            Interlocked.CompareExchange(ref _triggered, 0, 1);
        }

        return code;
    }

    public async Task<string> GetTypeName(long typeCode, CancellationToken cancellationToken)
    {
        string? typeName = await cache.GetTypeName(typeCode, cancellationToken);
        if (typeName != null) return typeName;

        await cache.Reset(cancellationToken);

        return await cache.GetTypeName(typeCode, cancellationToken) ?? typeCode.ToString();
    }
}
