using System.Text;
using Microsoft.Extensions.Hosting;
using Sa.Configuration.SecretStore.Engine;
using Sa.Configuration.SecretStore.Stores;

namespace Sa.Configuration.SecretStore;


/// <summary>
/// The Secrets class simplifies the management of sensitive information in your application
/// </summary>
public sealed class Secrets(params ISecretStore[] stores) : ISecretService
{
    public const string DefaultFileName = "secrets.txt";
    public const string EncryptedFileName = "secrets.pub";

    private const string DecryptionKeyVariable = "SA_SECRETS_DECRYPTION_KEY";
    private const string DecryptionKeyFileVariable = "SA_SECRETS_DECRYPTION_KEY_FILE";
    private const string ClientVariable = "SA_SECRETS_DECRYPTION_CLIENT";

    private readonly ChainedSecrets _chain = new(stores);

    public string? PopulateSecrets(string? inputString, bool returnNullIfSecretNotFound = false)
        => _chain.PopulateSecrets(inputString, returnNullIfSecretNotFound);


    public static Secrets CreateDefault(SecretOptions? options = null)
    {
        options ??= new();

        var filename = string.IsNullOrWhiteSpace(options.FileName)
            ? DefaultFileName
            : options.FileName;
        var environmentName = string.IsNullOrWhiteSpace(options.EnvironmentName)
            ? GetEnvironmentName()
            : options.EnvironmentName;
        var args = options.Args ?? Environment.GetCommandLineArgs();

        string prefixName = Path.GetFileNameWithoutExtension(filename);
        string extName = Path.GetExtension(filename);

        DotEnvSecretStore? dotEnv = null;
        DotEnvSecretStore? dotEnvEnvironment = null;

        if (options.LoadDotEnv)
        {
            string dotEnvFileName = string.IsNullOrWhiteSpace(options.DotEnvFileName)
                ? DotEnvSecretStore.DefaultFileName
                : options.DotEnvFileName;

            dotEnv = TryCreateDotEnv(dotEnvFileName);
            dotEnvEnvironment = TryCreateDotEnv($"{dotEnvFileName}.{environmentName}");
        }

        List<ISecretStore> chain =
        [
            new FileSecretStore(filename),
            new FileSecretStore($"{prefixName}.{environmentName}{extName}")
        ];

        chain.AddRange(CreateEncryptedStores(options, environmentName, dotEnv, dotEnvEnvironment));

        if (dotEnv is not null)
        {
            chain.Add(dotEnv);
        }

        if (dotEnvEnvironment is not null)
        {
            chain.Add(dotEnvEnvironment);
        }

        chain.Add(new EnvironmentVariableSecretStore());
        chain.Add(new CommandLineArgsSecretStore(args));

        return new([.. chain]);
    }

    public Secrets AddStore(ISecretStore store)
    {
        _chain.AddStore(store);
        return this;
    }

    public string? GetSecret(string key) => _chain.GetSecret(key);

    public static string GetEnvironmentName() =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("environment")
            ?? Environments.Production;

    private static List<ISecretStore> CreateEncryptedStores(
        SecretOptions options,
        string environmentName,
        DotEnvSecretStore? dotEnv,
        DotEnvSecretStore? dotEnvEnvironment)
    {
        List<ISecretStore> stores = [];
        SecretEncryptionOptions encryption = options.Encryption ?? new();

        if (!encryption.Enabled)
        {
            return stores;
        }

        bool hasEnvironmentFilePart = IsValidFileNamePart(environmentName);
        HashSet<string> seenPaths = new(StringComparer.Ordinal);

        AddStore(EncryptedFileName, client: null, expectedEnvironment: null);

        if (hasEnvironmentFilePart)
        {
            AddStore($"secrets.{environmentName}.pub", client: null, expectedEnvironment: environmentName);
        }

        foreach (string client in ResolveClients(encryption, dotEnv, dotEnvEnvironment))
        {
            AddStore($"secrets.{client}.pub", client, expectedEnvironment: null);

            if (hasEnvironmentFilePart)
            {
                AddStore($"secrets.{client}.{environmentName}.pub", client, expectedEnvironment: environmentName);
            }
        }

        return stores;

        void AddStore(string fileName, string? client, string? expectedEnvironment)
        {
            string? path = SecretFileResolver.GetFilePath(fileName);
            if (path is null || !seenPaths.Add(Path.GetFullPath(path)))
            {
                return;
            }

            string? decryptionKey = ResolveDecryptionKey(client, encryption, dotEnv, dotEnvEnvironment);
            if (decryptionKey is null)
            {
                throw new InvalidOperationException(BuildMissingKeyMessage(fileName, client));
            }

            stores.Add(new EncryptedFileSecretStore(path, decryptionKey, client, expectedEnvironment));
        }
    }

    private static IReadOnlyList<string> ResolveClients(
        SecretEncryptionOptions encryption,
        DotEnvSecretStore? dotEnv,
        DotEnvSecretStore? dotEnvEnvironment)
    {
        List<string> candidates = [];

        AddClients(candidates, GetEncryptionSetting(ClientVariable, dotEnv, dotEnvEnvironment));
        AddClients(candidates, encryption.Client);

        if (encryption.Clients is { Length: > 0 })
        {
            candidates.AddRange(encryption.Clients);
        }

        // Clients are loaded in order and the last one wins, so earlier duplicates are dropped.
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            string client = candidates[index].Trim();
            if (client.Length == 0 || !seen.Add(client))
            {
                continue;
            }

            result.Add(ValidateFileNamePart(client, "client"));
        }

        result.Reverse();
        return result;
    }

    private static void AddClients(List<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        target.AddRange(value.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? ResolveDecryptionKey(
        string? client,
        SecretEncryptionOptions encryption,
        DotEnvSecretStore? dotEnv,
        DotEnvSecretStore? dotEnvEnvironment)
    {
        if (client is not null
            && encryption.ClientKeys is not null
            && encryption.ClientKeys.TryGetValue(client, out string? clientKey)
            && !string.IsNullOrWhiteSpace(clientKey))
        {
            return clientKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(encryption.DecryptionKey))
        {
            return encryption.DecryptionKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(encryption.DecryptionKeyFile))
        {
            return ReadKeyFile(encryption.DecryptionKeyFile);
        }

        if (client is not null)
        {
            string clientKeyVariable = GetClientKeyVariable(client);

            string? key = GetEncryptionSetting(clientKeyVariable, dotEnv, dotEnvEnvironment);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key.Trim();
            }

            string? keyFile = GetEncryptionSetting(clientKeyVariable + "_FILE", dotEnv, dotEnvEnvironment);
            if (!string.IsNullOrWhiteSpace(keyFile))
            {
                return ReadKeyFile(keyFile.Trim());
            }
        }

        string? globalKey = GetEncryptionSetting(DecryptionKeyVariable, dotEnv, dotEnvEnvironment);
        if (!string.IsNullOrWhiteSpace(globalKey))
        {
            return globalKey.Trim();
        }

        string? globalKeyFile = GetEncryptionSetting(DecryptionKeyFileVariable, dotEnv, dotEnvEnvironment);
        if (!string.IsNullOrWhiteSpace(globalKeyFile))
        {
            return ReadKeyFile(globalKeyFile.Trim());
        }

        return null;
    }

    private static string? GetEncryptionSetting(
        string name,
        DotEnvSecretStore? dotEnv,
        DotEnvSecretStore? dotEnvEnvironment)
    {
        // A real environment variable always wins over a .env file.
        return Environment.GetEnvironmentVariable(name)
            ?? dotEnvEnvironment?.GetSecret(name)
            ?? dotEnv?.GetSecret(name);
    }

    private static string ReadKeyFile(string path)
    {
        string? resolved = SecretFileResolver.GetFilePath(path);
        if (resolved is null)
        {
            throw new InvalidOperationException($"Decryption key file '{path}' was not found.");
        }

        string key = File.ReadAllText(resolved).Trim();
        if (key.Length == 0)
        {
            throw new InvalidOperationException($"Decryption key file '{resolved}' is empty.");
        }

        return key;
    }

    private static string BuildMissingKeyMessage(string fileName, string? client)
    {
        string variables = client is null
            ? $"{DecryptionKeyVariable} or {DecryptionKeyFileVariable}"
            : $"{GetClientKeyVariable(client)} or {DecryptionKeyVariable}";

        return $"Encrypted secrets file '{fileName}' was found, but no decryption key is configured. "
            + $"Set {variables}, or disable the encrypted layer with SecretOptions.Encryption.Enabled = false.";
    }

    private static string GetClientKeyVariable(string client)
    {
        StringBuilder result = new(client.Length);

        foreach (char character in client.ToUpperInvariant())
        {
            result.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        return DecryptionKeyVariable + "_" + result;
    }

    private static string ValidateFileNamePart(string value, string kind)
    {
        if (!IsValidFileNamePart(value))
        {
            throw new InvalidOperationException(
                $"Invalid {kind} name '{value}'. Only letters, digits, '-', '_' and '.' are allowed, "
                + "and the name must not be '.' or '..'.");
        }

        return value;
    }

    private static bool IsValidFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static DotEnvSecretStore? TryCreateDotEnv(string path)
        => SecretFileResolver.GetFilePath(path) is null ? null : new DotEnvSecretStore(path);
}
