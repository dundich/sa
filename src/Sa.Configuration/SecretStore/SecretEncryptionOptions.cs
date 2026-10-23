namespace Sa.Configuration.SecretStore;

/// <summary>
/// Options for the encrypted secrets layer.
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is <see langword="true"/>, <see cref="Secrets.CreateDefault(SecretOptions?)"/>
/// looks for <c>secrets_enc.txt</c>, <c>secrets_enc.{env}.txt</c>, <c>secrets_enc.{client}.txt</c> and
/// <c>secrets_enc.{client}.{env}.txt</c>. A file that exists without a resolvable key fails startup.
/// </remarks>
public sealed record SecretEncryptionOptions
{
    /// <summary>Disables the encrypted layer entirely when set to <see langword="false"/>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Client name or a comma/semicolon separated client list. Clients are loaded in order;
    /// the last one wins.
    /// </summary>
    public string? Client { get; init; }

    /// <summary>Additional clients, loaded in order after <see cref="Client"/>.</summary>
    public string[]? Clients { get; init; }

    /// <summary>Global decryption key (passphrase or a raw key prefixed with <c>base64:</c>).</summary>
    public string? DecryptionKey { get; init; }

    /// <summary>Path to a file containing the global decryption key.</summary>
    public string? DecryptionKeyFile { get; init; }

    /// <summary>Per-client decryption keys; they take precedence over <see cref="DecryptionKey"/>.</summary>
    public IReadOnlyDictionary<string, string>? ClientKeys { get; init; }
}
