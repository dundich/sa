using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.CommandLine;

public static class Setup
{
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
