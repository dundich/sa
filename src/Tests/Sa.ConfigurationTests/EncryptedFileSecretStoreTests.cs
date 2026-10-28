using Sa.Configuration.SecretStore;
using Sa.Configuration.SecretStore.Stores;

namespace Sa.ConfigurationTests;

public class EncryptedFileSecretStoreTests
{
    private const string Key = "encrypted-file-store-passphrase";

    [Fact]
    public void Ctor_ReadsEncryptedValues()
    {
        using TempDirectory directory = new();
        string path = directory.Write(
            "secrets.lsr.pub",
            SecretsCipher.Encrypt(
                """
                # comment
                sa_pg_password=p@ss=word
                api_key=abc
                """,
                Key,
                client: "lsr"));

        var store = new EncryptedFileSecretStore(path, Key, client: "lsr");

        Assert.Equal("p@ss=word", store.GetSecret("sa_pg_password"));
        Assert.Equal("abc", store.GetSecret("api_key"));
        Assert.Null(store.GetSecret("missing"));
    }

    [Fact]
    public void Ctor_MissingFile_ReturnsEmptyStore()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "missing.txt");

        var store = new EncryptedFileSecretStore(path, Key);

        Assert.Null(store.GetSecret("any"));
    }

    [Fact]
    public void Ctor_WrongKey_ThrowsWithFilePath()
    {
        using TempDirectory directory = new();
        string path = directory.Write("secrets.pub", SecretsCipher.Encrypt("key=value", Key));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new EncryptedFileSecretStore(path, "wrong-key"));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains("could not be decrypted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_ClientMismatch_Throws()
    {
        using TempDirectory directory = new();
        string path = directory.Write("secrets.acme.pub", SecretsCipher.Encrypt("key=value", Key, client: "lsr"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new EncryptedFileSecretStore(path, Key, client: "acme"));

        Assert.Contains("client 'lsr'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_EnvironmentMismatch_Throws()
    {
        using TempDirectory directory = new();
        string path = directory.Write(
            "secrets.lsr.dev.pub",
            SecretsCipher.Encrypt("key=value", Key, client: "lsr", environment: "dev"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new EncryptedFileSecretStore(path, Key, client: "lsr", environment: "prod"));

        Assert.Contains("environment 'dev'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_PlaintextValue_Throws()
    {
        using TempDirectory directory = new();
        string path = directory.Write(
            "secrets.pub",
            """
            # saenc: v1
            # saenc: kdf=pbkdf2-sha256
            # saenc: iterations=600000
            # saenc: salt=AAAAAAAAAAAAAAAAAAAAAA==
            key=plain
            """);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new EncryptedFileSecretStore(path, Key));

        Assert.Contains("must not contain plaintext", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ctor_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new EncryptedFileSecretStore("secrets.pub", " "));
    }
}
