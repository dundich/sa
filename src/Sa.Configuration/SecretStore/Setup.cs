using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.SecretStore;

public static class Setup
{
    public static IConfigurationBuilder AddSaPostSecretProcessing(
        this IConfigurationBuilder builder,
        ISecretService secretService)
    {
        return builder.Add(new PostSecretProcessingConfigurationSource(
            () =>
            {
                ConfigurationBuilder postBuilder = new();
                foreach (var item in builder.Sources.Where(c => c is not PostSecretProcessingConfigurationSource))
                {
                    postBuilder.Add(item);
                }

                return postBuilder.Build();
            },
            secretService));
    }

    internal static IConfigurationBuilder AddSaPostSecretProcessing(
        this IConfigurationBuilder builder,
        SecretOptions? options = null)
            => builder.AddSaPostSecretProcessing(Secrets.CreateDefault(options));
}
