using Sa.Classes;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Mutable, canonical view over the merged configuration that the post-processing pass builds
/// once per <c>Load</c>. Every write from a transform lands here first, so later transforms see
/// it, and only what transforms actually wrote is copied into the provider's own data.
/// </summary>
internal sealed class MergedConfigurationView(Dictionary<string, string?> values) : ISaConfigurationView
{
    public IEnumerable<KeyValuePair<string, string?>> Values => values;

    public bool TryGetValue(string key, out string? value)
        => values.TryGetValue(ConfigurationKey.Normalize(key), out value);

    /// <summary>
    /// Records a transform's output so the next transform observes it. The key is normalized the
    /// same way lookups are, which keeps the working set to one entry per setting.
    /// </summary>
    public void Set(string key, string? value)
        => values[ConfigurationKey.Normalize(key)] = value;
}
