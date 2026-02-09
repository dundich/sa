using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.SecretStore;

internal sealed class PostSecretProcessingConfigurationSource(
    Func<IConfiguration> getInnerConfiguration,
    ISecretService secretService) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new PostSecretProcessingConfigurationProvider(getInnerConfiguration, secretService);
}
