using Microsoft.Extensions.Configuration;

namespace Sa.Configuration.SecretStore;

public static class Setup
{
    /// <summary>
    /// Adds a post-secrets-processing source: the final stage that substitutes <c>{{key}}</c>
    /// placeholders in every provider's values and, when <paramref name="keyAliasOptions"/>
    /// carries mappings, applies the explicit key aliases over the merged configuration.
    /// </summary>
    public static IConfigurationBuilder AddSaPostSecretProcessing(
        this IConfigurationBuilder builder,
        ISecretService secretService,
        KeyAliasOptions keyAliasOptions = null!)
    {
        if (keyAliasOptions is null)
            keyAliasOptions = new();
        else
            keyAliasOptions.Validate();

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
            secretService,
            keyAliasOptions));
    }

    internal static IConfigurationBuilder AddSaPostSecretProcessing(
        this IConfigurationBuilder builder,
        SecretOptions? options = null)
            => builder.AddSaPostSecretProcessing(
                Secrets.CreateDefault(options),
                (options ?? new()).KeyAliases);
}
