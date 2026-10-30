using Sa.Outbox.PostgreSql.Serialization;
using System.Text.Json;

namespace Sa.Outbox.PostgreSql.Configuration;

internal sealed class OutboxMessageSerializer : IOutboxMessageSerializer
{
#pragma warning disable IL2026
#pragma warning disable IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
    public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);


    public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
#pragma warning restore IL3050 // Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.
#pragma warning restore IL2026 // Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code

    public static OutboxMessageSerializer Instance { get; } = new();
}
