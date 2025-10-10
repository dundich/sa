namespace Sa.Outbox.PostgreSql.IdGen;

internal sealed class IdGenerator : IIdGenerator
{
    public string GenId(DateTimeOffset date) => Ulid.NewUlid(date).ToString();
}
