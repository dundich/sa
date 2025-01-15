using Sa.Extensions;
using Sa.Fixture;
using Sa.Outbox.PostgreSql.Serialization;
using System.Text.Json;

namespace Sa.Outbox.PostgreSqlTests.Serialization;


public class OutboxMessageSerializerTests(OutboxMessageSerializerTests.Fixture fixture) : IClassFixture<OutboxMessageSerializerTests.Fixture>
{

    public class Fixture : SaSubFixture<IOutboxMessageSerializer>
    {
        public Fixture() : base()
        {
            Services.AddOutboxMessageSerializer(new());
        }
    }


    private readonly IOutboxMessageSerializer sub = fixture.Sub;


    [Fact]
    public async Task Serialize_WithValidObject()
    {
        // Arrange
        var obj = new TestMessage { Id = 1, Content = "Test" };

        using var stream = new MemoryStream();
        // Act
        await sub.SerializeAsync(stream, obj);

        // Assert
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task Serialize_WithNullObject_ThrowsArgumentNullException()
    {
        using var stream = new MemoryStream();
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(()
            => sub.SerializeAsync<TestMessage>(stream, default!));
    }

    [Fact]
    public async Task Deserialize_WithValid_JsonReturnsObject()
    {
        // Arrange
        var json = "{\"Id\":1,\"Content\":\"Test\"}";

        // Act
        using var stream = new MemoryStream();
        stream.Write(json.StrToBytes());
        stream.Position = 0;

        TestMessage? message = await sub.DeserializeAsync<TestMessage>(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Equal(1, message.Id);
        Assert.Equal("Test", message.Content);
    }

    [Fact]
    public async Task Deserialize_WithInvalidJson_ThrowsJsonException()
    {
        // Arrange
        var invalidJson = "invalid json";

        using var stream = new MemoryStream();
        stream.Write(invalidJson.StrToBytes());
        stream.Position = 0;

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => sub.DeserializeAsync<TestMessage>(stream));
    }

    [Fact]
    public async Task Deserialize_WithNullJson_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => sub.DeserializeAsync<TestMessage>(default!));
    }


    private class TestMessage
    {
        public int Id { get; set; }
        public string? Content { get; set; }
    }
}