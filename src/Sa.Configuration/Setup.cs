using Microsoft.Extensions.Configuration;
using Sa.Configuration.CommandLine;
using Sa.Configuration.SecretStore;

namespace Sa.Configuration;

public static class Setup
{
    public static IConfigurationBuilder AddSaDefaultConfiguration(
        this IConfigurationBuilder builder, SecretOptions? options = null)
    {
        return builder
            .AddSaCommandLine(options?.Args)
            .AddSaPostSecretProcessing(options);
    }
}
