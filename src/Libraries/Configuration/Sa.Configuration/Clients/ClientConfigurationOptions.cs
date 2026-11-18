namespace Sa.Configuration.Clients;

/// <summary>
/// Behavior of the client registry.
/// </summary>
public sealed class ClientConfigurationOptions
{
    /// <summary>
    /// Creates every known client's overlay during <see cref="IClientConfigurations.ReloadAsync"/>
    /// instead of on first access. Off by default: providers are created lazily.
    /// </summary>
    public bool EagerLoad { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="IClientConfigurations.GetClient"/> throws for a client
    /// that is not registered or discovered. When <see langword="false"/> (default), such a client
    /// reads the core configuration, exactly like a client without overrides.
    /// </summary>
    public bool ThrowForUnknownClient { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default), a row whose value is <see cref="string.IsNullOrEmpty"/> or
    /// whitespace is dropped while the rows are read, so the client falls back to the core value — the
    /// same rule <c>SkipEmptyValues</c> applies to the core database layer. When
    /// <see langword="false"/>, an empty row is a value of its own and overrides the core one.
    /// </summary>
    /// <remarks>
    /// Only rows are affected. A blank a transform produced (an unresolved placeholder) is written
    /// after the rows are read and always stays local to the client.
    /// </remarks>
    public bool SkipEmptyValues { get; set; } = true;
}
