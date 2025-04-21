namespace Sa.Outbox.PostgreSql.IdGen;

public interface IIdGenerator
{
    string GenId(DateTimeOffset date);
}
