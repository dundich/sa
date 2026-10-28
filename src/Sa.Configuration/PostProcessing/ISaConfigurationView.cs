namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Read-only view of the merged configuration a post-processing transform runs over.
/// </summary>
/// <remarks>
/// Keys are exposed in their canonical spelling, with every environment-variable separator
/// (<c>__</c>) already translated to <c>:</c>, so a value stored as <c>sa__pg__host</c> and one
/// stored as <c>sa:pg:host</c> are the same entry. Lookups are case-insensitive for the same
/// reason the configuration system is.
/// </remarks>
public interface ISaConfigurationView
{
    /// <summary>
    /// Every key/value pair of the merged configuration, keyed by canonical spelling.
    /// </summary>
    IEnumerable<KeyValuePair<string, string?>> Values { get; }

    /// <summary>
    /// Case-insensitive, <c>__</c>-tolerant lookup of a single setting.
    /// </summary>
    /// <param name="key">Canonical or environment-variable spelling of the key.</param>
    /// <param name="value">The value when the key exists; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the key exists, even if its value is <see langword="null"/>.</returns>
    bool TryGetValue(string key, out string? value);
}
