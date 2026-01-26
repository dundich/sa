using Sa.Outbox.PostgreSql.Serialization;
using System.Text.Json;

namespace Sa.Outbox.PostgreSqlTests;

internal sealed class OutboxMessageSerializer : IOutboxMessageSerializer
{
    public T? Deserialize<T>(Stream stream)
    {
        return JsonSerializer.Deserialize<T>(stream);
    }

    public void Serialize<T>(Stream stream, T value)
    {
        JsonSerializer.Serialize(stream, value);
    }

    public static OutboxMessageSerializer Instance { get; } = new();
}
