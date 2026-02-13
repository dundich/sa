using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.SecretStore;

internal sealed class PostSecretProcessingConfigurationProvider(
    Func<IConfiguration> getInnerConfiguration,
    ISecretService secretService) : ConfigurationProvider
{

    private readonly Lazy<IConfiguration> _configuration = new(getInnerConfiguration);

    public override void Load()
    {
        foreach (KeyValuePair<string, string> child in _configuration.Value
        .AsEnumerable()
        .Where(c => c.Value is not null)
        .Cast<KeyValuePair<string, string>>())
        {
            Data[child.Key] = secretService.PopulateSecrets(child.Value, true);
        }
    }
}
