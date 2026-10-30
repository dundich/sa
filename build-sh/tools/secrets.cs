#:project ../../src/Libraries/Sa.Configuration/Sa.Configuration.csproj

using System.Globalization;
using System.Text;
using Sa.Configuration.SecretStore;
using Sa.Configuration.SecretStore.Stores;

try
{
    return Run(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        PrintHelp();
        return args.Length == 0 ? 1 : 0;
    }

    string command = args[0];
    Options options = Options.Parse(args[1..]);
    DotEnvSecretStore? dotEnv = File.Exists(DotEnvSecretStore.DefaultFileName)
        ? new DotEnvSecretStore()
        : null;

    return command switch
    {
        "encrypt" => Encrypt(options, dotEnv),
        "decrypt" => Decrypt(options, dotEnv),
        "check" => Check(options, dotEnv),
        _ => Unknown(command)
    };
}

static int Encrypt(Options options, DotEnvSecretStore? dotEnv)
{
    string input = RequireInput(options);
    string content = File.ReadAllText(input);
    string key = ResolveKey(options, client: options.Client, dotEnv);

    string encrypted = SecretsCipher.Encrypt(
        content,
        key,
        options.Client,
        options.Environment,
        options.Iterations);

    WriteOutput(options.Output, encrypted, input);
    return 0;
}

static int Decrypt(Options options, DotEnvSecretStore? dotEnv)
{
    string input = RequireInput(options);
    string content = File.ReadAllText(input);
    string key = ResolveKey(options, client: null, dotEnv);

    SecretsFileHeader header = SecretsCipher.ReadHeader(content);
    string plaintext = SecretsCipher.Decrypt(content, key, header.Client, header.Environment);

    WriteOutput(options.Output, plaintext, input);
    return 0;
}

static int Check(Options options, DotEnvSecretStore? dotEnv)
{
    string[] files = options.Positional.Count > 0
        ? [.. options.Positional]
        : Directory.GetFiles(".", "secrets*.pub");

    if (files.Length == 0)
    {
        Console.Error.WriteLine("No encrypted secrets files found.");
        return 0;
    }

    int failures = 0;

    foreach (string file in files.Order(StringComparer.Ordinal))
    {
        try
        {
            string content = File.ReadAllText(file);
            SecretsFileHeader header = SecretsCipher.ReadHeader(content);
            string key = ResolveKey(options, header.Client, dotEnv);

            SecretsCipher.Decrypt(content, key, header.Client, header.Environment);

            Console.WriteLine($"ok    {file} (client={header.Client ?? "-"}, env={header.Environment ?? "-"})");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine($"FAIL  {file}: {exception.Message}");
        }
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine(failures == 0
        ? $"Checked {files.Length} file(s): all decrypted."
        : $"Checked {files.Length} file(s): {failures} failed.");

    return failures == 0 ? 0 : 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintHelp();
    return 1;
}

static string RequireInput(Options options)
{
    if (options.Positional.Count == 0)
    {
        throw new InvalidOperationException("The input file is required.");
    }

    string input = options.Positional[0];
    if (!File.Exists(input))
    {
        throw new FileNotFoundException($"Input file '{input}' was not found.", input);
    }

    return input;
}

static void WriteOutput(string? output, string content, string input)
{
    if (string.IsNullOrWhiteSpace(output) || output == "-")
    {
        Console.Out.Write(content);
        return;
    }

    File.WriteAllText(output, content);
    Console.Error.WriteLine($"Wrote '{output}' (source: '{input}').");
}

static string ResolveKey(Options options, string? client, DotEnvSecretStore? dotEnv)
{
    if (!string.IsNullOrWhiteSpace(options.KeyFilePath))
    {
        string path = options.KeyFilePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Key file '{path}' was not found.", path);
        }

        string keyFromFile = File.ReadAllText(path).Trim();
        if (keyFromFile.Length == 0)
        {
            throw new InvalidOperationException($"Key file '{path}' is empty.");
        }

        return keyFromFile;
    }

    if (!string.IsNullOrWhiteSpace(client))
    {
        string? clientKey = Setting("SA_SECRETS_DECRYPTION_KEY_" + ToVariablePart(client), dotEnv);
        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            return clientKey.Trim();
        }
    }

    string? key = Setting(options.KeyEnvironmentVariable, dotEnv);
    if (!string.IsNullOrWhiteSpace(key))
    {
        return key.Trim();
    }

    if (!Console.IsInputRedirected)
    {
        return PromptKey();
    }

    throw new InvalidOperationException(
        $"No decryption key found. Set {options.KeyEnvironmentVariable}, use --key-file, "
        + "or run interactively to be prompted.");
}

static string? Setting(string name, DotEnvSecretStore? dotEnv)
    => Environment.GetEnvironmentVariable(name) ?? dotEnv?.GetSecret(name);

static string PromptKey()
{
    Console.Error.Write("Decryption key: ");
    StringBuilder key = new();

    while (true)
    {
        ConsoleKeyInfo pressed = Console.ReadKey(intercept: true);

        if (pressed.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return key.ToString();
        }

        if (pressed.Key == ConsoleKey.Backspace)
        {
            if (key.Length > 0)
            {
                key.Length--;
            }

            continue;
        }

        key.Append(pressed.KeyChar);
    }
}

static string ToVariablePart(string client)
{
    StringBuilder result = new(client.Length);

    foreach (char character in client.ToUpperInvariant())
    {
        result.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
    }

    return result.ToString();
}

static void PrintHelp()
{
    Console.Error.WriteLine("""
        sa-secrets — encrypt, decrypt and check Sa.Configuration secrets files.

        Usage:
          do-secrets.sh encrypt <input> [options]
          do-secrets.sh decrypt <input> [options]
          do-secrets.sh check [files...] [options]

        Options:
          -o, --output <path>    Output file; omit or use '-' for stdout.
          --client <name>        Client written to the header (encrypt only).
          --env <name>           Environment written to the header (encrypt only).
          --iterations <n>       PBKDF2 iterations for a new file (default: 600000).
          --key-env <name>       Environment variable with the key
                                 (default: SA_SECRETS_DECRYPTION_KEY).
          --key-file <path>      File with the key.
          -h, --help             Show this help.

        The key is never accepted as a command-line value: it would leak through
        process listings and shell history. Set SA_SECRETS_DECRYPTION_KEY,
        SA_SECRETS_DECRYPTION_KEY_<CLIENT>, put it in .env, use --key-file,
        or run interactively to be prompted.

        'check' exits with a non-zero code when any file fails to decrypt.
        """);
}

internal sealed class Options
{
    public List<string> Positional { get; } = [];

    public string? Output { get; private set; }

    public string? Client { get; private set; }

    public string? Environment { get; private set; }

    public int Iterations { get; private set; } = SecretsCipher.DefaultIterations;

    public string KeyEnvironmentVariable { get; private set; } = "SA_SECRETS_DECRYPTION_KEY";

    public string? KeyFilePath { get; private set; }

    public static Options Parse(string[] args)
    {
        Options options = new();

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "-o" or "--output":
                    options.Output = NextValue(args, ref index, argument);
                    break;

                case "--client":
                    options.Client = NextValue(args, ref index, argument);
                    break;

                case "--env":
                    options.Environment = NextValue(args, ref index, argument);
                    break;

                case "--iterations":
                    string value = NextValue(args, ref index, argument);
                    options.Iterations = int.Parse(value, CultureInfo.InvariantCulture);
                    break;

                case "--key-env":
                    options.KeyEnvironmentVariable = NextValue(args, ref index, argument);
                    break;

                case "--key-file":
                    options.KeyFilePath = NextValue(args, ref index, argument);
                    break;

                case "--key":
                    throw new InvalidOperationException(
                        "Refusing --key: a key passed on the command line leaks through process listings "
                        + "and shell history. Use --key-file or an environment variable.");

                default:
                    if (argument.StartsWith('-') && argument != "-")
                    {
                        throw new InvalidOperationException($"Unknown option '{argument}'.");
                    }

                    options.Positional.Add(argument);
                    break;
            }
        }

        return options;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Option '{option}' requires a value.");
        }

        return args[++index];
    }
}
