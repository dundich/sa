namespace Sa.Configuration.PostProcessing;

/// <summary>
/// The per-key form of a transform: each rule maps the current value of one key to its new value.
/// </summary>
/// <remarks>
/// A rule runs only for a key present in the layer it runs over, so it never invents a setting; use
/// the delegate overload of <c>AddSaConfigurationTransform</c> to add or expand keys. A rule that
/// returns the value it received is not reported as a change, so an untouched key is never copied
/// into the pass data — the core pass keeps reading the source, and a client overlay keeps reading
/// the core.
/// <para>
/// Only the pairs the layer owns are considered. In the core pass that is every key, but a client
/// overlay exposes only the client's own rows: a key the client does not own has already been
/// transformed by the core pass, so its rule is not run a second time over that output.
/// </para>
/// </remarks>
internal sealed class ValueTransform(IReadOnlyDictionary<string, Func<string?, string?>> rules)
    : ISaConfigurationTransform
{
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
    {
        foreach (KeyValuePair<string, string?> pair in configuration.Values)
        {
            if (!rules.TryGetValue(pair.Key, out Func<string?, string?>? map))
                continue;

            string? changed = map(pair.Value);

            // Unchanged values stay out of the pass data: in the core they would shadow the source
            // with a copy, and in a client overlay they would freeze a copy of the core value.
            if (string.Equals(pair.Value, changed, StringComparison.Ordinal))
                continue;

            yield return new KeyValuePair<string, string?>(pair.Key, changed);
        }
    }
}
