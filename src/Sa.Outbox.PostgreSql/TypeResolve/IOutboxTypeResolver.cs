namespace Sa.Outbox.PostgreSql.TypeResolve;

internal interface IOutboxTypeResolver
{
    Task<long> GetHashCode(string typeName, CancellationToken cancellationToken);
    Task<string> GetTypeName(long typeHashCode, CancellationToken cancellationToken);
}
