namespace Sa.Configuration.PostProcessing;

/// <summary>
/// The per-key form of a transform: each rule maps the current value of one key to its new value.
/// </summary>
/// <remarks>
/// A rule runs only for a key that exists, so it never invents a setting; use the delegate overload
/// of <c>AddSaConfigurationTransform</c> to add or expand keys. A rule that returns the value it
/// received is not reported as a change, so
/// an untouched key is never copied into the pass data — the core pass keeps reading the source, and
/// a client overlay keeps reading the core.
/// </remarks>
internal sealed class ValueTransform(IReadOnlyDictionary<string, Func<string?, string?>> rules)
    : ISaConfigurationTransform
{
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
    {
        foreach (KeyValuePair<string, Func<string?, string?>> rule in rules)
        {
            // No key, nothing to map. Adding a key is the delegate form's job, not a rule's.
            if (!configuration.TryGetValue(rule.Key, out string? value))
                continue;

            string? changed = rule.Value(value);

            // Unchanged values stay out of the pass data: in the core they would shadow the source
            // with a copy, and in a client overlay they would freeze a copy of the core value.
            if (string.Equals(value, changed, StringComparison.Ordinal))
                continue;

            yield return new KeyValuePair<string, string?>(rule.Key, changed);
        }
    }
}
