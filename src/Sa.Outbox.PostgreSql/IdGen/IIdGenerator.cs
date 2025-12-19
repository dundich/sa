namespace Sa.Outbox.PostgreSql.IdGen;

public interface IIdGenerator
{
    Guid GenId(DateTimeOffset timestamp);
}
