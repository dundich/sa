namespace Sa.Configuration.CommandLine;

using Microsoft.Extensions.Configuration;
using Sa.Classes;


internal sealed class ArgumentsConfigurationSource : IConfigurationSource
{
    public string[]? Args { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new ArgumentsConfigurationProvider(this);
    }
}

/// <summary>
/// Провайдер конфигурации для аргументов командной строки.
/// </summary>
internal class ArgumentsConfigurationProvider(ArgumentsConfigurationSource source) : ConfigurationProvider
{
    private readonly Arguments Arguments = Arguments.CreateDefault(source.Args);


    public override void Load()
    {
        foreach (var kvp in Arguments.Parameters)
        {
            Data[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Answers for the environment-variable spelling as well: arguments are stored under the
    /// canonical <c>:</c> key, and <c>configuration["sa__pg__host"]</c> must find what
    /// <c>--sa__pg__host</c> supplied.
    /// </summary>
    public override bool TryGet(string key, out string? value)
        => base.TryGet(ConfigurationKey.Normalize(key), out value);
}
