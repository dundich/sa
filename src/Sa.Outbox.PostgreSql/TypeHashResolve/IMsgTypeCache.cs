
namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal interface IMsgTypeCache
{
    ValueTask<long> GetCode(string typeName, CancellationToken cancellationToken);
    ValueTask<string?> GetTypeName(long code, CancellationToken cancellationToken);
    ValueTask Reset(CancellationToken cancellationToken);
}