using Microsoft.Extensions.Configuration;
using Sa.Classes;

namespace Sa.Configuration.SecretStore;

internal sealed class PostSecretProcessingConfigurationProvider(
    Func<IConfiguration> getInnerConfiguration,
    ISecretService secretService,
    KeyAliasOptions keyAliasOptions) : ConfigurationProvider
{

    private readonly Lazy<IConfiguration> _configuration = new(getInnerConfiguration);

    public override void Load()
    {
        // The merged snapshot of every provider: ConfigurationSection.GetChildren() unions the
        // keys each provider claims, and the value of each key is resolved last-provider-wins —
        // so this is the true merged configuration, which the alias then corrects in place.
        foreach (var child in _configuration.Value
            .AsEnumerable()
            .Where(c => c.Value is not null))
        {
            Data[child.Key] = secretService.PopulateSecrets(child.Value, true);
        }

        ApplyKeyAliases();
    }

    /// <summary>
    /// Writes the alias value to its canonical configuration path. Runs last, over the merged
    /// configuration of every provider, so the alias can fill a canonical path no single source
    /// holds — e.g. a flat key in one provider and the canonical path in another.
    /// </summary>
    /// <remarks>
    /// A non-blank alias value is written to the canonical path — or only to a canonical path
    /// that is empty or absent when <see cref="KeyAliasOptions.CanonicalValueWins"/> is set, so a
    /// value any provider already filled wins over the alias. A missing or blank alias value
    /// never touches the canonical path. The alias key itself is left in place, so the flat
    /// spelling stays readable directly. Keys are compared the same way the configuration
    /// system does — case-insensitively, and with <c>__</c> normalized to the <c>:</c> separator,
    /// so both spellings of a mapping address the same key.
    /// </remarks>
    private void ApplyKeyAliases()
    {
        if (keyAliasOptions.KeyAliases.Count == 0)
            return;

        foreach (KeyValuePair<string, string> mapping in keyAliasOptions.KeyAliases)
        {
            string canonicalKey = ConfigurationKey.Normalize(mapping.Key);
            string aliasKey = ConfigurationKey.Normalize(mapping.Value);

            if (!TryFindValue(aliasKey, out string? aliasValue)
                || string.IsNullOrWhiteSpace(aliasValue))
            {
                continue;
            }

            if (keyAliasOptions.CanonicalValueWins
                && TryFindValue(canonicalKey, out string? canonicalValue)
                && !string.IsNullOrWhiteSpace(canonicalValue))
            {
                continue;
            }

            Data[FindOrAddKey(canonicalKey)] = aliasValue;
        }
    }

    /// <summary>
    /// Case-insensitive, separator-normalizing lookup over <see cref="ConfigurationProvider.Data"/>
    /// (which is ordinal), so a key stored as <c>sa__pg__host</c> is found as <c>sa:pg:host</c>.
    /// Mirrors the scan <see cref="Microsoft.Extensions.Configuration.MemoryConfigurationProvider"/>
    /// performs for the same reason.
    /// </summary>
    private bool TryFindValue(string key, out string? value)
    {
        foreach (KeyValuePair<string, string?> entry in Data)
        {
            if (ConfigurationKey.Normalize(entry.Key).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Returns the existing <see cref="ConfigurationProvider.Data"/> key for <paramref name="key"/>
    /// in whatever case and spelling it was stored, or <paramref name="key"/> itself when absent —
    /// so writing the alias value updates the stored entry instead of adding a duplicate.
    /// </summary>
    private string FindOrAddKey(string key)
    {
        foreach (string entryKey in Data.Keys)
        {
            if (entryKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                || ConfigurationKey.Normalize(entryKey).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return entryKey;
            }
        }

        return key;
    }
}
