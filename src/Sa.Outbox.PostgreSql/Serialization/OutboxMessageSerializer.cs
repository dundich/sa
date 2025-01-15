using Sa.Serialization.Converter;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Sa.Outbox.PostgreSql.Serialization;

/// <summary>
/// Implementation of <see cref="IOutboxMessageSerializer"/> using <see cref="JsonSerializer"/>.
/// </summary>
internal class OutboxMessageSerializer : IOutboxMessageSerializer
{

    private readonly static JavaScriptEncoder encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic);


    /// <summary>
    /// <see cref="JsonSerializerOptions"/> options for the JSON serializer. By default adds <see cref="ObjectToInferredTypesConverter"/> converter.
    /// </summary>
    public JsonSerializerOptions Options { get; private set; } = CreateDefaultOptions();

    public OutboxMessageSerializer WithOptions(JsonSerializerOptions? options)
    {
        if (options != null)
        {
            Options = options;
        }
        return this;
    }

    public static JsonSerializerOptions CreateDefaultOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
            Encoder = encoder,
        };
        options.Converters.Add(new ObjectToInferredTypesConverter());
        return options;
    }

    public Task<T?> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default)
        => JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).AsTask();

    public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream, Options);


    public Task SerializeAsync<T>(Stream stream, [NotNull] T value, CancellationToken cancellationToken = default)
        => JsonSerializer.SerializeAsync<T>(stream, value ?? throw new ArgumentNullException(nameof(value)), Options, cancellationToken);

    public void Serialize<T>(Stream stream, [NotNull] T value) => JsonSerializer.Serialize<T>(stream, value ?? throw new ArgumentNullException(nameof(value)), Options);
}
