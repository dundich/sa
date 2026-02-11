using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Publisher;


public class OutboxPublisherTests(OutboxPublisherTests.Fixture fixture)
    : IClassFixture<OutboxPublisherTests.Fixture>
{
    public class Fixture : OutboxPostgreSqlFixture<IOutboxMessagePublisher>
    {
    }

    private IOutboxMessagePublisher Sub => fixture.Sub;


    [Fact]
    public async Task Publish_MultipleMessages_ReturnsExpectedResult()
    {
        Console.Write(fixture.ConnectionString);

        ulong result = await Sub.PublishSingle(new TestMessage { PayloadId = "1", Content = "Message 1", TenantId = 1 }, 1, TestContext.Current.CancellationToken);

        result += await Sub.PublishSingle(new TestMessage { PayloadId = "2", Content = "Message 3", TenantId = 2 }, 2, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, (int)result);

        var sql = $"select count(*) from {PgOutboxTableSettings.Defaults.DatabaseTableName}{PgOutboxTableSettings.MessageTable.Suffix}";
        int count = await fixture.DataSource.ExecuteReaderFirst<int>(sql, TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }
}
