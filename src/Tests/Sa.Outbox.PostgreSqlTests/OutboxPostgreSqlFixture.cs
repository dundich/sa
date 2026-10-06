using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxPostgreSqlFixture<TSub> : PgDataSourceFixture<TSub>
     where TSub : notnull
{
    public OutboxPostgreSqlFixture()
    {
        Services.AddSaOutboxUsingPostgreSql(builder => builder
            .WithDataSource(b => b.Configure(o => o.ConnectionString = ConnectionString))
            .WithMessageSerializer(_ => OutboxMessageSerializer.Instance)
        );
    }
}
