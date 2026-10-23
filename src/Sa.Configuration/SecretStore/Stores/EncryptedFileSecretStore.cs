using Sa.Configuration.SecretStore.Engine;

namespace Sa.Configuration.SecretStore.Stores;

/// <summary>
/// Loads secrets from an encrypted <c>key=value</c> file.
/// </summary>
/// <remarks>
/// The file format is produced by <see cref="SecretsCipher"/>. A missing file yields an empty
/// store, so callers can decide whether the file is required. An existing but undecryptable
/// file always throws — silently falling back to lower-priority stores would hide misconfiguration.
/// </remarks>
public sealed class EncryptedFileSecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets;

    /// <param name="path">Relative or absolute path to the encrypted file.</param>
    /// <param name="decryptionKey">Passphrase or a raw key prefixed with <c>base64:</c>.</param>
    /// <param name="client">Expected client from the file header; <see langword="null"/> requires no client.</param>
    /// <param name="environment">Expected environment from the file header; <see langword="null"/> requires no environment.</param>
    public EncryptedFileSecretStore(
        string path,
        string decryptionKey,
        string? client = null,
        string? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(decryptionKey);

        string? filepath = SecretFileResolver.GetFilePath(path);
        if (filepath is null)
        {
            _secrets = [];
            return;
        }

        string encryptedContent = File.ReadAllText(filepath);
        string plaintextContent;

        try
        {
            plaintextContent = SecretsCipher.Decrypt(encryptedContent, decryptionKey, client, environment);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Encrypted secrets file '{filepath}' could not be decrypted: {exception.Message}",
                exception);
        }

        _secrets = SecretFileParser.Parse(plaintextContent.Split('\n'));
    }

    public string? GetSecret(string key)
    {
        _secrets.TryGetValue(key, out var value);
        return value;
    }
}
