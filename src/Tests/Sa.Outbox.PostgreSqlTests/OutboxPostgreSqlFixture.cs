using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxPostgreSqlFixture<TSub> : PgDataSourceFixture<TSub>
     where TSub : notnull
{
    public OutboxPostgreSqlFixture()
    {
        Services.AddOutboxUsingPostgreSql(builder => builder
            .ConfigureDataSource(b => b.WithConnectionString(sp => ConnectionString))
            .WithMessageSerializer(sp => new OutboxMessageSerializer())
        );
    }
}
