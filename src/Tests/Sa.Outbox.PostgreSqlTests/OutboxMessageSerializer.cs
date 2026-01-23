using System.Text.Json;
using Sa.Outbox.PostgreSql.Serialization;

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
