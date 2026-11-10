using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.CommandLine;

/// <summary>
/// Registers the command-line configuration source.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Adds configuration values parsed from command-line arguments.
    /// </summary>
    /// <param name="builder">The configuration builder to extend.</param>
    /// <param name="args">The arguments to parse; <see langword="null"/> uses the process arguments.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    public static IConfigurationBuilder AddSaCommandLine(
        this IConfigurationBuilder builder,
        string[]? args = null)
    {
        return builder.Add(new ArgumentsConfigurationSource
        {
            Args = args
        });
    }
}
