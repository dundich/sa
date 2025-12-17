namespace Sa.Outbox.PostgreSqlTests.Publisher;


public class OutboxPublisherTests(OutboxPublisherTests.Fixture fixture) : IClassFixture<OutboxPublisherTests.Fixture>
{
    public class Fixture : OutboxPostgreSqlFixture<IOutboxMessagePublisher>
    {
    }

    private IOutboxMessagePublisher Sub => fixture.Sub;


    [Fact]
    public async Task Publish_MultipleMessages_ReturnsExpectedResult()
    {
        Console.Write(fixture.ConnectionString);

        // Arrange
        List<TestMessage> messages =
        [
            new TestMessage { PayloadId = "1", Content = "Message 1", TenantId = 1},
            new TestMessage { PayloadId = "2", Content = "Message 2", TenantId = 2}
        ];

        // Act
        ulong result = await Sub.Publish(messages, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, (int)result);

        int count = await fixture.DataSource.ExecuteReaderFirst<int>("select count(*) from outbox__msg$", TestContext.Current.CancellationToken);
        Assert.Equal(2, count);
    }
}
