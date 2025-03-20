using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PostgreSql.Serialization;

public interface IOutboxMessageSerializer
{
    T? Deserialize<T>(Stream stream);
    Task<T?> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default);

    void Serialize<T>(Stream stream, [NotNull] T value);
    Task SerializeAsync<T>(Stream stream, [NotNull] T value, CancellationToken cancellationToken = default);
}
