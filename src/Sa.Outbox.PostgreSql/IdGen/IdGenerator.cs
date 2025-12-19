namespace Sa.Outbox.PostgreSql.IdGen;

internal sealed class IdGenerator : IIdGenerator
{
    public Guid GenId(DateTimeOffset timestamp) 
        => Guid.CreateVersion7(timestamp);
}
