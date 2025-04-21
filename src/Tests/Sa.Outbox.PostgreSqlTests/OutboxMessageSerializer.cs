using Sa.Outbox.PostgreSql.Serialization;
using System.Text.Json;

namespace Sa.Outbox.PostgreSqlTests;

public class OutboxMessageSerializer : IOutboxMessageSerializer
{
    public T? Deserialize<T>(Stream stream)
    {
        return JsonSerializer.Deserialize<T>(stream);
    }

    public void Serialize<T>(Stream stream, T value)
    {
        JsonSerializer.Serialize(stream, value);
    }
}