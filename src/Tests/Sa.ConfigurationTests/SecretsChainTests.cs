using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

public class SecretsChainTests
{
    private const string Key = "chain-test-passphrase";

    [Fact]
    public void CreateDefault_EncryptedLayer_IsOverriddenByDotEnvAndEnvironmentAndArgs()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string environment = "env" + suffix;
        string client = "client" + suffix;
        string secretKey = "priority_key_" + suffix;
        string plainName = $"secrets_{suffix}.txt";
        string environmentName = $"secrets_{suffix}.{environment}.txt";
        string encryptedName = $"secrets.{client}.{environment}.pub";

        using TempDirectory directory = new();
        string dotEnvPath = Path.Combine(directory.Path, ".env");
        string dotEnvEnvironmentPath = dotEnvPath + "." + environment;

        string plainPath = Path.Combine(AppContext.BaseDirectory, plainName);
        string environmentPath = Path.Combine(AppContext.BaseDirectory, environmentName);
        string encryptedPath = Path.Combine(AppContext.BaseDirectory, encryptedName);

        File.WriteAllText(plainPath, $"{secretKey}=plain");
        File.WriteAllText(environmentPath, $"{secretKey}=plain-env");
        File.WriteAllText(encryptedPath, SecretsCipher.Encrypt($"{secretKey}=encrypted", Key, client, environment));
        File.WriteAllText(dotEnvPath, $"{secretKey}=dotenv");
        File.WriteAllText(dotEnvEnvironmentPath, $"{secretKey}=dotenv-env");

        string? previousValue = Environment.GetEnvironmentVariable(secretKey);

        try
        {
            SecretOptions options = new()
            {
                FileName = plainName,
                EnvironmentName = environment,
                LoadDotEnv = false,
                DotEnvFileName = dotEnvPath,
                Encryption = new SecretEncryptionOptions
                {
                    Client = client,
                    ClientKeys = new Dictionary<string, string> { [client] = Key }
                }
            };

            // Encrypted files override both plaintext files.
            Assert.Equal("encrypted", Secrets.CreateDefault(options).GetSecret(secretKey));

            // .env.{env} overrides .env, which overrides the encrypted layer.
            options = options with { LoadDotEnv = true };
            Assert.Equal("dotenv-env", Secrets.CreateDefault(options).GetSecret(secretKey));

            File.Delete(dotEnvEnvironmentPath);
            Assert.Equal("dotenv", Secrets.CreateDefault(options).GetSecret(secretKey));

            // A real environment variable overrides every file.
            Environment.SetEnvironmentVariable(secretKey, "process");
            Assert.Equal("process", Secrets.CreateDefault(options).GetSecret(secretKey));

            // Command-line arguments are the highest priority.
            options = options with { Args = ["--" + secretKey, "cli"] };
            Assert.Equal("cli", Secrets.CreateDefault(options).GetSecret(secretKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretKey, previousValue);
            File.Delete(plainPath);
            File.Delete(environmentPath);
            File.Delete(encryptedPath);
            File.Delete(dotEnvPath);
            File.Delete(dotEnvEnvironmentPath);
        }
    }

    [Fact]
    public void CreateDefault_ReadsDecryptionKeyFromDotEnv()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string environment = "env" + suffix;
        string client = "client" + suffix;
        string secretKey = "dotenv_key_" + suffix;
        string encryptedName = $"secrets.{client}.{environment}.pub";
        string encryptedPath = Path.Combine(AppContext.BaseDirectory, encryptedName);

        using TempDirectory directory = new();
        string dotEnvPath = directory.Write(".env", $"SA_SECRETS_DECRYPTION_KEY={Key}\n");

        File.WriteAllText(encryptedPath, SecretsCipher.Encrypt($"{secretKey}=from-dotenv", Key, client, environment));

        using EnvironmentScope scope = new("SA_SECRETS_DECRYPTION_KEY", GetClientKeyVariable(client));

        try
        {
            var options = new SecretOptions
            {
                EnvironmentName = environment,
                LoadDotEnv = true,
                DotEnvFileName = dotEnvPath,
                Encryption = new SecretEncryptionOptions { Client = client }
            };

            Assert.Equal("from-dotenv", Secrets.CreateDefault(options).GetSecret(secretKey));
        }
        finally
        {
            File.Delete(encryptedPath);
        }
    }

    [Fact]
    public void CreateDefault_Throws_WhenEncryptedFileHasNoKey()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string client = "client" + suffix;
        string fileName = $"secrets.{client}.pub";
        string filePath = Path.Combine(AppContext.BaseDirectory, fileName);

        File.WriteAllText(filePath, SecretsCipher.Encrypt("key=value", Key, client));

        using EnvironmentScope scope = new(
            "SA_SECRETS_DECRYPTION_KEY",
            "SA_SECRETS_DECRYPTION_KEY_FILE",
            GetClientKeyVariable(client));

        try
        {
            var options = new SecretOptions
            {
                EnvironmentName = "env" + suffix,
                LoadDotEnv = false,
                Encryption = new SecretEncryptionOptions { Client = client }
            };

            var exception = Assert.Throws<InvalidOperationException>(() => Secrets.CreateDefault(options));

            Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
            Assert.Contains(GetClientKeyVariable(client), exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void CreateDefault_DisabledEncryption_IgnoresEncryptedFiles()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string client = "client" + suffix;
        string fileName = $"secrets.{client}.pub";
        string filePath = Path.Combine(AppContext.BaseDirectory, fileName);

        File.WriteAllText(filePath, SecretsCipher.Encrypt("key=value", Key, client));

        try
        {
            var options = new SecretOptions
            {
                EnvironmentName = "env" + suffix,
                LoadDotEnv = false,
                Encryption = new SecretEncryptionOptions { Enabled = false, Client = client }
            };

            Assert.Null(Secrets.CreateDefault(options).GetSecret("key"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void CreateDefault_NoEncryptedFiles_DoesNotRequireAKey()
    {
        var options = new SecretOptions
        {
            EnvironmentName = "env" + Guid.NewGuid().ToString("N"),
            LoadDotEnv = false,
            Encryption = new SecretEncryptionOptions { Client = "client" + Guid.NewGuid().ToString("N") }
        };

        Assert.Null(Secrets.CreateDefault(options).GetSecret("missing"));
    }

    [Fact]
    public void EncryptedFileName_UsesCommittedPubExtension()
    {
        Assert.Equal("secrets.pub", Secrets.EncryptedFileName);
    }

    [Fact]
    public void CreateDefault_ReadsEnvironmentOnlyEncryptedFile()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string environment = "env" + suffix;
        string secretKey = "env_key_" + suffix;
        string fileName = $"secrets.{environment}.pub";
        string filePath = Path.Combine(AppContext.BaseDirectory, fileName);

        File.WriteAllText(filePath, SecretsCipher.Encrypt($"{secretKey}=env-only", Key, environment: environment));

        try
        {
            var options = new SecretOptions
            {
                EnvironmentName = environment,
                LoadDotEnv = false,
                Encryption = new SecretEncryptionOptions { DecryptionKey = Key }
            };

            Assert.Equal("env-only", Secrets.CreateDefault(options).GetSecret(secretKey));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static string GetClientKeyVariable(string client)
        => "SA_SECRETS_DECRYPTION_KEY_" + client.ToUpperInvariant();

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public EnvironmentScope(params string[] names)
        {
            foreach (string name in names)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach ((string name, string? value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
