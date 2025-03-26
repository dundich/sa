namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal interface IMsgTypeHashResolver
{
    Task<long> GetCode(string typeName, CancellationToken cancellationToken);
    Task<string> GetTypeName(long typeCode, CancellationToken cancellationToken);
}
