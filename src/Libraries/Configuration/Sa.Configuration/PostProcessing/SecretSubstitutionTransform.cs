using Sa.Configuration.SecretStore;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Replaces <c>{{secret}}</c> placeholders in configuration values with the resolved secret.
/// </summary>
/// <remarks>
/// Only values that actually need work are returned: one that contains a placeholder or is
/// blank. Everything else is left to the provider that supplied it, so the transform never
/// shadows an unrelated setting. A blank value becomes <see langword="null"/>, and a
/// placeholder that cannot be resolved becomes <see langword="null"/> as well — the softer
/// behaviour of the configuration chain, as opposed to calling
/// <see cref="ISecretService.PopulateSecrets"/> directly.
/// </remarks>
public sealed class SecretSubstitutionTransform : ISaConfigurationTransform
{
    private readonly Lazy<ISecretService> _secretService;

    /// <summary>
    /// Creates the transform over a ready secret service.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="secretService"/> is null.</exception>
    public SecretSubstitutionTransform(ISecretService secretService)
        : this(() => secretService ?? throw new ArgumentNullException(nameof(secretService)))
    {
    }

    /// <summary>
    /// Creates the transform over a factory, so the service can be resolved lazily — the
    /// configuration chain builds it on the first load rather than at registration time, and
    /// repeated <c>AddSaConfiguration</c> calls can hand over their final options.
    /// </summary>
    internal SecretSubstitutionTransform(Func<ISecretService> secretServiceFactory)
        => _secretService = new Lazy<ISecretService>(
            secretServiceFactory ?? throw new ArgumentNullException(nameof(secretServiceFactory)));

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ISecretService secretService = _secretService.Value;

        // Snapshot: the caller records our output in the same view we are reading from.
        foreach (KeyValuePair<string, string?> pair in configuration.Values.ToArray())
        {
            if (pair.Value is null)
                continue;

            if (!pair.Value.Contains("{{", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            yield return new(pair.Key, secretService.PopulateSecrets(pair.Value, true));
        }
    }
}
