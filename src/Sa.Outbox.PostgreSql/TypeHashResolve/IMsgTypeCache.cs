
namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal interface IMsgTypeCache
{
    Task<long> GetCode(string typeName, CancellationToken cancellationToken);
    Task<string?> GetTypeName(long code, CancellationToken cancellationToken);
    Task Reset(CancellationToken cancellationToken);
}