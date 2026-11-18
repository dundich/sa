namespace Sa.Configuration.Clients;

/// <summary>
/// One client setting loaded from a provider: the client it belongs to, the key with the
/// <b>same name</b> as the core configuration, and the value.
/// </summary>
/// <remarks>
/// The flat row shape is what a query returns (<c>client_id, key, value</c>), so a provider can
/// hand over the whole table at once and the registry groups the rows by
/// <see cref="ClientId"/>. Keys carry no prefix, no section and no wrapping; they use the same
/// spelling as the core configuration, and the environment-variable form (<c>sa__pg__host</c>) is
/// read as the path (<c>sa:pg:host</c>).
/// </remarks>
public sealed record ClientSetting
{
    /// <summary>
    /// Creates a row.
    /// </summary>
    /// <param name="clientId">Name the client is addressed by. Case-insensitive.</param>
    /// <param name="key">Setting name, identical to the core configuration's key.</param>
    /// <param name="value">The client's value.</param>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> or <paramref name="key"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public ClientSetting(string clientId, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        ClientId = clientId;
        Key = key;
        Value = value;
    }

    /// <summary>Name the client is addressed by.</summary>
    public string ClientId { get; init; }

    /// <summary>Setting name, identical to the core configuration's key.</summary>
    public string Key { get; init; }

    /// <summary>The client's value.</summary>
    public string Value { get; init; }
}
