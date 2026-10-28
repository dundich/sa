using Microsoft.Extensions.Configuration;
using Sa.Classes;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Runs the transform pipeline over the merged configuration of every other provider and stores
/// only the values the transforms produced.
/// </summary>
/// <remarks>
/// Keeping the provider's data to the derived keys is deliberate: the configuration root merges
/// providers last-in-wins, so a pass that copied the whole configuration would shadow whatever an
/// earlier pass derived, and a source registered later would silently beat an alias. Here a key
/// this pass does not touch simply falls through to the provider that owns it.
/// <para>
/// <see cref="Data"/> is replaced on every load rather than mutated, so <c>Reload()</c> drops a
/// key that disappeared from its source or from the alias map instead of serving it forever.
/// </para>
/// </remarks>
internal sealed class SaPostProcessingConfigurationProvider(
    Func<IConfiguration> getInnerConfiguration,
    SaConfigurationState state) : ConfigurationProvider
{
    private readonly Lazy<IConfiguration> _configuration = new(getInnerConfiguration);

    public override void Load()
    {
        // The merged snapshot of every other provider. Keys are canonicalized here once, which
        // makes every later lookup independent of how a source spelled a separator.
        Dictionary<string, string?> working = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string?> pair in _configuration.Value.AsEnumerable())
        {
            if (pair.Value is null)
                continue;

            working[ConfigurationKey.Normalize(pair.Key)] = pair.Value;
        }

        MergedConfigurationView view = new(working);
        Dictionary<string, string?> output = new(StringComparer.OrdinalIgnoreCase);

        foreach (ISaConfigurationTransform transform in state.BuildTransforms())
        {
            // Snapshot: writing to the view is how the next transform sees this one's output.
            foreach (KeyValuePair<string, string?> change in transform.Apply(view).ToArray())
            {
                string key = ConfigurationKey.Normalize(change.Key);
                output[key] = change.Value;
                view.Set(key, change.Value);
            }
        }

        Data = output;
    }

    /// <summary>
    /// Answers for both spellings of a key, so <c>configuration["sa__pg__host"]</c> finds what the
    /// pass stored as <c>sa:pg:host</c>.
    /// </summary>
    public override bool TryGet(string key, out string? value)
        => base.TryGet(ConfigurationKey.Normalize(key), out value);

    /// <summary>
    /// The keys this pass actually produced. <see cref="ConfigurationProvider.Data"/> is protected,
    /// so this is the seam the tests use to prove unchanged keys are not copied.
    /// </summary>
    internal ICollection<string> StoredKeys => Data.Keys;
}
