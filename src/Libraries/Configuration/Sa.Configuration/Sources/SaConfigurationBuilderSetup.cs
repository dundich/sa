using Microsoft.Extensions.Configuration;
using Sa.Configuration.Sources;

namespace Sa.Configuration;

/// <summary>
/// Entry point of the layer assembly.
/// </summary>
public static class SaConfigurationBuilderSetup
{
    /// <summary>
    /// Configures the Sa configuration pipeline fluently: static sources first, dynamic sources
    /// (overriding the static values) after them, then the secret/alias pass and the clients.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Configuration.AddSaConfiguration(sa =>
    /// {
    ///     sa.UseStandardSources();
    ///     sa.UseSecrets();
    ///     sa.UseDynamic(pgSource);
    ///     sa.UseClients();
    /// });
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static SaConfigurationBuilder AddSaConfiguration(
        this IConfigurationBuilder builder,
        Action<SaConfigurationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        SaConfigurationBuilder sa = SaConfigurationBuilder.Create(builder);
        configure(sa);

        return sa;
    }
}
