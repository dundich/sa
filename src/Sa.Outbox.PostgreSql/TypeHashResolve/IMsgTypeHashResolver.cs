namespace Sa.Outbox.PostgreSql.TypeHashResolve;

internal interface IMsgTypeHashResolver
{
    ValueTask<long> GetCode(string typeName, CancellationToken cancellationToken);
    ValueTask<string> GetTypeName(long typeCode, CancellationToken cancellationToken);
}
