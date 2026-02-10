namespace Sa.Configuration.CommandLine;

using Microsoft.Extensions.Configuration;


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
}
