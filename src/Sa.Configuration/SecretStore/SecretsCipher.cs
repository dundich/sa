using System.Security.Cryptography;
using System.Text;

namespace Sa.Configuration.SecretStore;

/// <summary>
/// Header metadata of an encrypted secrets file.
/// </summary>
/// <param name="Kdf">Key derivation function: <c>pbkdf2-sha256</c> or <c>none</c>.</param>
/// <param name="Iterations">PBKDF2 iterations; <c>0</c> for a raw key.</param>
/// <param name="Client">Client the file belongs to, if declared.</param>
/// <param name="Environment">Environment the file belongs to, if declared.</param>
public sealed record SecretsFileHeader(string Kdf, int Iterations, string? Client, string? Environment);

/// <summary>
/// Encrypts and decrypts <c>key=value</c> secrets files.
/// </summary>
/// <remarks>
/// <para>
/// The format is a text file with a <c># saenc: v1</c> header followed by regular
/// <c>key=value</c> lines where every value is stored as <c>ENC1:&lt;base64&gt;</c>.
/// Comments and blank lines are preserved, so the file stays readable and diff-friendly.
/// </para>
/// <para>
/// Values are encrypted with AES-256-GCM. The per-file key is derived from the passphrase
/// with PBKDF2-SHA256 and a random 16-byte salt; every value gets a random 12-byte nonce.
/// The key name is authenticated as associated data, so a value cannot be moved to another key.
/// A raw 32-byte key can be supplied with the <c>base64:</c> prefix instead of a passphrase.
/// </para>
/// </remarks>
public static class SecretsCipher
{
    /// <summary>Prefix of an encrypted value.</summary>
    public const string ValuePrefix = "ENC1:";

    /// <summary>Format version used in the associated data.</summary>
    public const string Version = "SAENC1";

    /// <summary>Default number of PBKDF2 iterations.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Minimum accepted number of PBKDF2 iterations.</summary>
    public const int MinimumIterations = 100_000;

    /// <summary>Prefix of a raw base64 key.</summary>
    public const string RawKeyPrefix = "base64:";

    internal const string HeaderPrefix = "# saenc:";

    private const string HeaderVersion = HeaderPrefix + " v1";
    private const string KdfPbkdf2 = "pbkdf2-sha256";
    private const string KdfNone = "none";
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>
    /// Encrypts every value of a plaintext <c>key=value</c> document.
    /// </summary>
    /// <param name="plaintextContent">Source document; comments and blank lines are preserved.</param>
    /// <param name="key">Passphrase or a raw key prefixed with <c>base64:</c>.</param>
    /// <param name="client">Optional client name written to the header.</param>
    /// <param name="environment">Optional environment name written to the header.</param>
    /// <param name="iterations">PBKDF2 iterations for a passphrase; ignored for a raw key.</param>
    public static string Encrypt(
        string plaintextContent,
        string key,
        string? client = null,
        string? environment = null,
        int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(plaintextContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        ValidateMetadataValue(client, nameof(client));
        ValidateMetadataValue(environment, nameof(environment));

        string normalizedKey = NormalizeKey(key);
        bool rawKey = IsRawKey(normalizedKey);

        if (!rawKey && iterations < MinimumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                iterations,
                $"PBKDF2 iterations must be at least {MinimumIterations}.");
        }

        byte[] salt = rawKey ? [] : RandomNumberGenerator.GetBytes(SaltSize);
        byte[] masterKey = DeriveKey(normalizedKey, salt, iterations);

        try
        {
            StringBuilder result = new();

            AppendHeader(result, rawKey, iterations, salt, client, environment);

            foreach (string originalLine in SplitLines(plaintextContent))
            {
                string line = originalLine.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    if (line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The source document is already encrypted.");
                    }

                    result.Append(originalLine).Append('\n');
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid secrets file line: '{originalLine}'. Expected 'key=value'.");
                }

                string name = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();

                if (name.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid secrets file line: '{originalLine}'. The key is empty.");
                }

                if (value.StartsWith(ValuePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The secret '{name}' is already encrypted.");
                }

                result
                    .Append(name)
                    .Append('=')
                    .Append(ValuePrefix)
                    .Append(EncryptValue(name, value, masterKey))
                    .Append('\n');
            }

            return result.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>
    /// Decrypts an encrypted <c>key=value</c> document.
    /// </summary>
    /// <param name="encryptedContent">Encrypted document.</param>
    /// <param name="key">Passphrase or a raw key prefixed with <c>base64:</c>.</param>
    /// <param name="expectedClient">
    /// Client expected from the header. Pass <see langword="null"/> to require an absent client.
    /// </param>
    /// <param name="expectedEnvironment">
    /// Environment expected from the header. Pass <see langword="null"/> to require an absent environment.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The document is malformed, the key is wrong, or the header does not match the expectations.
    /// </exception>
    public static string Decrypt(
        string encryptedContent,
        string key,
        string? expectedClient = null,
        string? expectedEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(encryptedContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string normalizedKey = NormalizeKey(key);
        string[] lines = SplitLines(encryptedContent);
        ParsedHeader header = ParseHeader(lines, out int firstDataLine);

        if (!string.Equals(header.Client, EmptyToNull(expectedClient), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The encrypted document belongs to client '{header.Client ?? "<none>"}', "
                + $"expected '{EmptyToNull(expectedClient) ?? "<none>"}'.");
        }

        if (!string.Equals(header.Environment, EmptyToNull(expectedEnvironment), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The encrypted document belongs to environment '{header.Environment ?? "<none>"}', "
                + $"expected '{EmptyToNull(expectedEnvironment) ?? "<none>"}'.");
        }

        bool rawKey = IsRawKey(normalizedKey);
        if (rawKey != header.IsRawKey)
        {
            throw new InvalidOperationException(
                header.IsRawKey
                    ? "The document was encrypted with a raw key; provide it with the 'base64:' prefix."
                    : "The document was encrypted with a passphrase; a raw 'base64:' key cannot decrypt it.");
        }

        byte[] masterKey = DeriveKey(normalizedKey, header.Salt, header.Iterations);

        try
        {
            StringBuilder result = new();

            for (int index = firstDataLine; index < lines.Length; index++)
            {
                string originalLine = lines[index];
                string line = originalLine.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    result.Append(originalLine).Append('\n');
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid secrets file line: '{originalLine}'. Expected 'key=value'.");
                }

                string name = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();

                if (name.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Invalid secrets file line: '{originalLine}'. The key is empty.");
                }

                if (!value.StartsWith(ValuePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The value of '{name}' is not encrypted. Encrypted secrets files must not contain plaintext values.");
                }

                result
                    .Append(name)
                    .Append('=')
                    .Append(DecryptValue(name, value[ValuePrefix.Length..], masterKey))
                    .Append('\n');
            }

            return result.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>
    /// Reads the header of an encrypted document without decrypting it.
    /// </summary>
    public static SecretsFileHeader ReadHeader(string encryptedContent)
    {
        ArgumentNullException.ThrowIfNull(encryptedContent);

        ParsedHeader header = ParseHeader(SplitLines(encryptedContent), out _);

        return new SecretsFileHeader(
            header.IsRawKey ? KdfNone : KdfPbkdf2,
            header.IsRawKey ? 0 : header.Iterations,
            header.Client,
            header.Environment);
    }

    internal static byte[] DeriveKey(string key, byte[] salt, int iterations)
    {
        if (IsRawKey(key))
        {
            byte[] rawKey;
            try
            {
                rawKey = Convert.FromBase64String(key[RawKeyPrefix.Length..].Trim());
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("The raw decryption key is not valid base64.", nameof(key), exception);
            }

            if (rawKey.Length != KeySize)
            {
                CryptographicOperations.ZeroMemory(rawKey);
                throw new ArgumentException(
                    $"A raw decryption key must be {KeySize} bytes long, got {rawKey.Length}.",
                    nameof(key));
            }

            return rawKey;
        }

        if (iterations < MinimumIterations)
        {
            throw new InvalidOperationException(
                $"PBKDF2 iterations must be at least {MinimumIterations}, got {iterations}.");
        }

        return Rfc2898DeriveBytes.Pbkdf2(key, salt, iterations, HashAlgorithmName.SHA256, KeySize);
    }

    internal static string EncryptValue(string name, string value, byte[] masterKey, ReadOnlySpan<byte> nonce)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(value);
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintext.Length];

        try
        {
            using AesGcm aes = new(masterKey, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, CreateAssociatedData(name));

            byte[] blob = new byte[NonceSize + TagSize + ciphertext.Length];
            nonce.CopyTo(blob.AsSpan(0, NonceSize));
            tag.CopyTo(blob, NonceSize);
            ciphertext.CopyTo(blob, NonceSize + TagSize);

            return Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static string DecryptValue(string name, string base64, byte[] masterKey)
    {
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"The secret '{name}' contains invalid base64.", exception);
        }

        if (blob.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException($"The secret '{name}' is truncated.");
        }

        ReadOnlySpan<byte> nonce = blob.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = blob.AsSpan(NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = blob.AsSpan(NonceSize + TagSize);
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using AesGcm aes = new(masterKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, CreateAssociatedData(name));

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                $"Failed to decrypt the secret '{name}'. The decryption key is wrong or the file is corrupted.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string EncryptValue(string name, string value, byte[] masterKey)
        => EncryptValue(name, value, masterKey, RandomNumberGenerator.GetBytes(NonceSize));

    private static void AppendHeader(
        StringBuilder result,
        bool rawKey,
        int iterations,
        byte[] salt,
        string? client,
        string? environment)
    {
        result.Append(HeaderVersion).Append('\n');
        result.Append(HeaderPrefix).Append(" kdf=").Append(rawKey ? KdfNone : KdfPbkdf2).Append('\n');

        if (!rawKey)
        {
            result.Append(HeaderPrefix).Append(" iterations=").Append(iterations).Append('\n');
            result.Append(HeaderPrefix).Append(" salt=").Append(Convert.ToBase64String(salt)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(client))
        {
            result.Append(HeaderPrefix).Append(" client=").Append(client.Trim()).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            result.Append(HeaderPrefix).Append(" env=").Append(environment.Trim()).Append('\n');
        }
    }

    private static ParsedHeader ParseHeader(string[] lines, out int firstDataLine)
    {
        int index = 0;

        while (index < lines.Length && lines[index].Trim().Length == 0)
        {
            index++;
        }

        if (index >= lines.Length)
        {
            throw new InvalidOperationException("The encrypted secrets file is empty.");
        }

        string firstLine = lines[index].Trim().TrimStart('\uFEFF');
        if (!string.Equals(firstLine, HeaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The file is not a valid encrypted secrets file: expected the '{HeaderVersion}' header.");
        }

        index++;

        bool hasKdf = false;
        bool hasIterations = false;
        bool hasSalt = false;
        bool hasClient = false;
        bool hasEnvironment = false;
        bool isRawKey = false;
        int iterations = 0;
        byte[] salt = [];
        string? client = null;
        string? environment = null;

        while (index < lines.Length)
        {
            string line = lines[index].Trim();
            if (!line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            {
                break;
            }

            string metadata = line[HeaderPrefix.Length..].Trim();
            int separator = metadata.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Invalid encrypted file header line: '{line}'.");
            }

            string name = metadata[..separator].Trim();
            string value = metadata[(separator + 1)..].Trim();

            switch (name)
            {
                case "kdf":
                    if (hasKdf)
                    {
                        throw new InvalidOperationException("Duplicate 'kdf' header entry.");
                    }

                    hasKdf = true;
                    isRawKey = value switch
                    {
                        KdfPbkdf2 => false,
                        KdfNone => true,
                        _ => throw new InvalidOperationException($"Unsupported key derivation function '{value}'.")
                    };
                    break;

                case "iterations":
                    if (hasIterations)
                    {
                        throw new InvalidOperationException("Duplicate 'iterations' header entry.");
                    }

                    hasIterations = true;
                    if (!int.TryParse(value, out iterations) || iterations < MinimumIterations)
                    {
                        throw new InvalidOperationException(
                            $"'iterations' must be an integer greater than or equal to {MinimumIterations}.");
                    }

                    break;

                case "salt":
                    if (hasSalt)
                    {
                        throw new InvalidOperationException("Duplicate 'salt' header entry.");
                    }

                    hasSalt = true;
                    try
                    {
                        salt = Convert.FromBase64String(value);
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidOperationException("The 'salt' header entry is not valid base64.", exception);
                    }

                    break;

                case "client":
                    if (hasClient)
                    {
                        throw new InvalidOperationException("Duplicate 'client' header entry.");
                    }

                    hasClient = true;
                    // The value is validated against the expected file name in Decrypt.
                    client = EmptyToNull(value);
                    break;

                case "env":
                    if (hasEnvironment)
                    {
                        throw new InvalidOperationException("Duplicate 'env' header entry.");
                    }

                    hasEnvironment = true;
                    environment = EmptyToNull(value);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown encrypted file header entry '{name}'.");
            }

            index++;
        }

        if (!hasKdf)
        {
            throw new InvalidOperationException("The encrypted file header does not declare a 'kdf'.");
        }

        if (!isRawKey)
        {
            if (!hasIterations)
            {
                throw new InvalidOperationException("The encrypted file header does not declare 'iterations'.");
            }

            if (!hasSalt)
            {
                throw new InvalidOperationException("The encrypted file header does not declare a 'salt'.");
            }

            if (salt.Length != SaltSize)
            {
                throw new InvalidOperationException($"The salt must be {SaltSize} bytes long.");
            }
        }

        firstDataLine = index;

        return new ParsedHeader(isRawKey, iterations, salt, client, environment);
    }

    private static string[] SplitLines(string content)
    {
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');

        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    private static string NormalizeKey(string key) => key.Trim();

    private static bool IsRawKey(string key) => key.StartsWith(RawKeyPrefix, StringComparison.Ordinal);

    private static byte[] CreateAssociatedData(string name)
        => Encoding.UTF8.GetBytes($"{Version}|{name}");

    private static void ValidateMetadataValue(string? value, string parameterName)
    {
        if (value is not null && (value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException("Header values must not contain line breaks.", parameterName);
        }
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ParsedHeader(
        bool IsRawKey,
        int Iterations,
        byte[] Salt,
        string? Client,
        string? Environment);
}
