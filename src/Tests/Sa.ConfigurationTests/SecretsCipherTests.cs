using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

public class SecretsCipherTests
{
    private const string Passphrase = "unit-test-passphrase";

    private static readonly byte[] ZeroSalt = new byte[16];
    private static readonly byte[] ZeroNonce = new byte[12];

    private static readonly string ZeroSaltBase64 = Convert.ToBase64String(ZeroSalt);


    [Fact]
    public void EncryptValue_MatchesKnownAnswer()
    {
        byte[] masterKey = SecretsCipher.DeriveKey(Passphrase, ZeroSalt, SecretsCipher.MinimumIterations);

        // Generated independently with Python's cryptography package:
        // PBKDF2-HMAC-SHA256(passphrase, salt, 100000, 32) + AESGCM(blob = nonce || tag || ciphertext).
        string encrypted = SecretsCipher.EncryptValue("api_key", "secret", masterKey, ZeroNonce);

        Assert.Equal("AAAAAAAAAAAAAAAAB3HXnCWfDzDJQQxyFm91q5lhWQsEPQ==", encrypted);
    }

    [Fact]
    public void EncryptValue_UnicodeAndEquals_MatchesKnownAnswer()
    {
        byte[] masterKey = SecretsCipher.DeriveKey(Passphrase, ZeroSalt, SecretsCipher.MinimumIterations);
        byte[] nonce = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

        string encrypted = SecretsCipher.EncryptValue("db_password", "pä£s=word", masterKey, nonce);

        Assert.Equal("AAECAwQFBgcICQoLbXTcfJ47nCu7Qn7CTPspDMNlXGPBsWoxYBus", encrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_PreservesCommentsAndOrder()
    {
        const string plaintext = """
            # Postgres
            sa_pg_password=superSecret123
            # API key with '=' inside
            api_key=abc=123
            empty=
            unicode=Привет
            """;

        string encrypted = SecretsCipher.Encrypt(plaintext, Passphrase, "lsr", "dev");
        string decrypted = SecretsCipher.Decrypt(encrypted, Passphrase, "lsr", "dev");

        Assert.StartsWith("# saenc: v1", encrypted, StringComparison.Ordinal);
        Assert.Contains("sa_pg_password=ENC1:", encrypted, StringComparison.Ordinal);
        Assert.Contains("api_key=ENC1:", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("superSecret123", encrypted, StringComparison.Ordinal);

        Assert.Equal(
            """
            # Postgres
            sa_pg_password=superSecret123
            # API key with '=' inside
            api_key=abc=123
            empty=
            unicode=Привет

            """,
            decrypted);
    }

    [Fact]
    public void Encrypt_EmptyDocument_ProducesHeaderOnlyFile()
    {
        string encrypted = SecretsCipher.Encrypt(string.Empty, Passphrase);

        Assert.StartsWith("# saenc: v1", encrypted, StringComparison.Ordinal);
        Assert.Equal(string.Empty, SecretsCipher.Decrypt(encrypted, Passphrase));
    }

    [Fact]
    public void Encrypt_RejectsAlreadyEncryptedDocument()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Encrypt(encrypted, Passphrase));

        Assert.Contains("already encrypted", exception.Message);
    }

    [Fact]
    public void Encrypt_RejectsMalformedLine()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Encrypt("no separator here", Passphrase));

        Assert.Contains("Expected 'key=value'", exception.Message);
    }

    [Fact]
    public void Encrypt_RejectsLineBreaksInMetadata()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => SecretsCipher.Encrypt("key=value", Passphrase, client: "lsr\nevil=1"));

        Assert.Equal("client", exception.ParamName);
    }

    [Fact]
    public void Encrypt_RejectsTooFewIterations()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SecretsCipher.Encrypt("key=value", Passphrase, iterations: 1000));

        Assert.Equal("iterations", exception.ParamName);
    }

    [Fact]
    public void Decrypt_RejectsPlaintextValue()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);
        string tampered = encrypted.Replace("key=ENC1:", "key=plain", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(tampered, Passphrase));

        Assert.Contains("must not contain plaintext", exception.Message);
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(encrypted, "another-passphrase"));

        Assert.Contains("Failed to decrypt", exception.Message);
    }

    [Fact]
    public void Decrypt_TamperedValue_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);
        string[] lines = encrypted.Split('\n');
        int dataLine = Array.FindIndex(lines, line => line.StartsWith("key=", StringComparison.Ordinal));
        lines[dataLine] = lines[dataLine][..^2] + "AA";

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(string.Join('\n', lines), Passphrase));

        Assert.Contains("Failed to decrypt", exception.Message);
    }

    [Fact]
    public void Decrypt_ValueMovedToAnotherKey_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);
        string moved = encrypted.Replace("key=ENC1:", "other=ENC1:", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(moved, Passphrase));

        Assert.Contains("Failed to decrypt", exception.Message);
    }

    [Fact]
    public void Decrypt_ClientMismatch_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase, client: "lsr");

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(encrypted, Passphrase, expectedClient: "acme"));

        Assert.Contains("client 'lsr'", exception.Message);
    }

    [Fact]
    public void Decrypt_EnvironmentMismatch_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase, environment: "dev");

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(encrypted, Passphrase, expectedEnvironment: "prod"));

        Assert.Contains("environment 'dev'", exception.Message);
    }

    [Fact]
    public void Decrypt_HeaderMetadataPresentButNotExpected_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase, client: "lsr");

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(encrypted, Passphrase));

        Assert.Contains("expected '<none>'", exception.Message);
    }

    [Fact]
    public void Decrypt_TooFewIterationsInHeader_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);
        string tampered = encrypted.Replace(
            "iterations=600000",
            "iterations=1000",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(tampered, Passphrase));

        Assert.Contains("iterations", exception.Message);
    }

    [Fact]
    public void Decrypt_UnknownHeaderEntry_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);
        string tampered = encrypted.Replace(
            "# saenc: kdf=",
            "# saenc: unknown=1\n# saenc: kdf=",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(tampered, Passphrase));

        Assert.Contains("Unknown encrypted file header entry", exception.Message);
    }

    [Fact]
    public void Decrypt_MissingKdf_Throws()
    {
        const string content = """
            # saenc: v1
            key=ENC1:AAAA
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(content, Passphrase));

        Assert.Contains("'kdf'", exception.Message);
    }

    [Fact]
    public void RawKey_RoundTrip()
    {
        byte[] rawKey = new byte[32];
        rawKey[0] = 42;
        string key = "base64:" + Convert.ToBase64String(rawKey);

        string encrypted = SecretsCipher.Encrypt("key=value", key);
        string decrypted = SecretsCipher.Decrypt(encrypted, key);

        Assert.Contains("# saenc: kdf=none", encrypted, StringComparison.Ordinal);
        Assert.StartsWith("key=value", decrypted, StringComparison.Ordinal);
    }

    [Fact]
    public void RawKey_WrongLength_Throws()
    {
        string validKey = "base64:" + Convert.ToBase64String(new byte[32]);
        string encrypted = SecretsCipher.Encrypt("key=value", validKey);
        string shortKey = "base64:" + Convert.ToBase64String(new byte[16]);

        var exception = Assert.Throws<ArgumentException>(
            () => SecretsCipher.Decrypt(encrypted, shortKey));

        Assert.Contains("32 bytes", exception.Message);
    }

    [Fact]
    public void PassphraseDocument_WithRawKey_Throws()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase);
        string rawKey = "base64:" + Convert.ToBase64String(new byte[32]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(encrypted, rawKey));

        Assert.Contains("raw 'base64:' key", exception.Message);
    }

    [Fact]
    public void RawKeyDocument_WithPassphrase_Throws()
    {
        string rawKey = "base64:" + Convert.ToBase64String(new byte[32]);
        string encrypted = SecretsCipher.Encrypt("key=value", rawKey);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SecretsCipher.Decrypt(encrypted, Passphrase));

        Assert.Contains("raw key", exception.Message);
    }

    [Fact]
    public void ReadHeader_ReturnsMetadata()
    {
        string encrypted = SecretsCipher.Encrypt("key=value", Passphrase, "lsr", "dev");

        SecretsFileHeader header = SecretsCipher.ReadHeader(encrypted);

        Assert.Equal("pbkdf2-sha256", header.Kdf);
        Assert.Equal(SecretsCipher.DefaultIterations, header.Iterations);
        Assert.Equal("lsr", header.Client);
        Assert.Equal("dev", header.Environment);
    }

    [Fact]
    public void ReadHeader_RawKey_HasNoIterations()
    {
        string rawKey = "base64:" + Convert.ToBase64String(new byte[32]);
        string encrypted = SecretsCipher.Encrypt("key=value", rawKey);

        SecretsFileHeader header = SecretsCipher.ReadHeader(encrypted);

        Assert.Equal("none", header.Kdf);
        Assert.Equal(0, header.Iterations);
    }

    [Fact]
    public void Salt_IsPerFile_AndRandom()
    {
        string first = SecretsCipher.Encrypt("key=value", Passphrase);
        string second = SecretsCipher.Encrypt("key=value", Passphrase);

        string firstSalt = first.Split('\n').Single(line => line.StartsWith("# saenc: salt=", StringComparison.Ordinal));
        string secondSalt = second.Split('\n').Single(line => line.StartsWith("# saenc: salt=", StringComparison.Ordinal));

        Assert.NotEqual(firstSalt, secondSalt);
        Assert.Equal(ZeroSaltBase64.Length, firstSalt["# saenc: salt=".Length..].Length);
    }
}
