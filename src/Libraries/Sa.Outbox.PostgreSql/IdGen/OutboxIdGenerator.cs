namespace Sa.Outbox.PostgreSql.IdGen;

internal sealed class OutboxIdGenerator : IOutboxIdGenerator
{
    public Guid GenId(DateTimeOffset timestamp)
        => Guid.CreateVersion7(timestamp);
}
