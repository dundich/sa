namespace Sa.Outbox.PostgreSql.IdGen;

public interface IOutboxIdGenerator
{
    Guid GenId(DateTimeOffset timestamp);
}
