using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PostgreSql.Serialization;

public interface IOutboxMessageSerializer
{
    T? Deserialize<T>(Stream stream);
    void Serialize<T>(Stream stream, [NotNull] T value);
}
