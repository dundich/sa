
namespace Sa.Outbox.PostgreSql.TypeResolve;

internal interface IOutboxTypeCache
{
    Task<long> GetCode(string typeName, CancellationToken cancellationToken);
    Task<string?> GetTypeName(long code, CancellationToken cancellationToken);
    Task Reset(CancellationToken cancellationToken);
}
