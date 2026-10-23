using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.SecretStore;

internal sealed class PostSecretProcessingConfigurationSource(
    Func<IConfiguration> getInnerConfiguration,
    ISecretService secretService,
    KeyAliasOptions keyAliasOptions) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new PostSecretProcessingConfigurationProvider(getInnerConfiguration, secretService, keyAliasOptions);
}
