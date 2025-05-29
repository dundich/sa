namespace Sa.Outbox.PostgreSql.IdGen;

internal class IdGenerator : IIdGenerator
{
    public string GenId(DateTimeOffset date) => Ulid.NewUlid(date).ToString();
}
