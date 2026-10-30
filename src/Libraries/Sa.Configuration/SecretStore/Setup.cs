using Microsoft.Extensions.Configuration;
using Sa.Configuration.PostProcessing;

namespace Sa.Configuration.SecretStore;

public static class Setup
{
    /// <summary>
    /// Adds a post-secrets-processing source: the final stage that substitutes <c>{{key}}</c>
    /// placeholders in every provider's values and, when <paramref name="keyAliasOptions"/>
    /// carries mappings, applies the explicit key aliases over the merged configuration.
    /// </summary>
    /// <remarks>
    /// Unlike <c>AddSaConfiguration</c>, every call adds its own pass, so repeated calls compose
    /// additively. Use <c>AddSaConfiguration</c> when the calls should accumulate into one pass.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or
    /// <paramref name="secretService"/> is null.</exception>
    /// <exception cref="ArgumentException">A mapping key or alias is blank.</exception>
    public static IConfigurationBuilder AddSaPostSecretProcessing(
        this IConfigurationBuilder builder,
        ISecretService secretService,
        KeyAliasOptions? keyAliasOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(secretService);

        SaConfigurationState state = new();
        state.UseSecretService(secretService);

        if (keyAliasOptions is not null)
            state.AddAliases(keyAliasOptions);

        return builder.Add(new SaPostProcessingConfigurationSource(
            () => PostProcessingPipeline.BuildInner(builder),
            state));
    }
}
