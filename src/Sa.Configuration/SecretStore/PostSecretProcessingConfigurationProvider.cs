using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.SecretStore;

internal sealed class PostSecretProcessingConfigurationProvider(
    Func<IConfiguration> getInnerConfiguration,
    ISecretService secretService) : ConfigurationProvider
{

    public Lazy<IConfiguration> _configuration = new(getInnerConfiguration);

    public override void Load()
        => LoadConfigurationSection(_configuration.Value, string.Empty);

    private void LoadConfigurationSection(IConfiguration config, string prefix)
    {
        foreach (var child in config.GetChildren())
        {
            var key = string.IsNullOrEmpty(prefix)
                ? child.Key
                : $"{prefix}{ConfigurationPath.KeyDelimiter}{child.Key}";

            if (child.Value != null)
            {
                Data[key] = secretService.PopulateSecrets(child.Value, true);
            }

            LoadConfigurationSection(child, key);
        }
    }
}
