using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxPostgreSqlFixture<TSub> : PgDataSourceFixture<TSub>
     where TSub : notnull
{
    public OutboxPostgreSqlFixture()
    {
        Services.AddOutboxUsingPostgreSql(builder 
            => builder.AddDataSource(b 
                => b.WithConnectionString(sp => ConnectionString)));
    }
}
