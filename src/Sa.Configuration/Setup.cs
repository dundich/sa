using Microsoft.Extensions.Configuration;
using Sa.Configuration.SecretStore;

namespace Sa.Configuration;


public static class Setup
{
    public static IConfigurationBuilder AddDefaultSaConfiguration(this IConfigurationBuilder builder, params IReadOnlyCollection<string> jsonFiles)
    {
        builder.SetBasePath(Directory.GetCurrentDirectory());

        foreach (var file in jsonFiles) 
        {
            builder.AddJsonFile(file, optional: true);
        }

        builder
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{SaEnvironment.Default.EnvironmentName}.json", optional: true)
            .AddEnvironmentVariables();

        return builder;
    }

    public static string? PopulateSecrets(this IConfiguration configuration, string key)
        => Secrets.Service.PopulateSecrets(configuration[key]);
}
