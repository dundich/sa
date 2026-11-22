namespace Sa.Configuration.PostProcessing;

/// <summary>
/// One registered rule: the mapping function and whether it opted in to values that came from the
/// core fallback of a client overlay.
/// </summary>
internal readonly record struct ValueRule(
    Func<ValueTransformContext, string?, string?> Map,
    bool HandlesCoreFallback);

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
/// In a client overlay a key can be present because an earlier transform read it from the core
/// fallback. That value has already been transformed by the core pass, so the simple overload skips
/// it and the context overload receives <see cref="ValueTransformContext.IsCoreFallback"/> set to
/// <see langword="true"/> and decides for itself.
/// </para>
/// </remarks>
internal sealed class ValueTransform(IReadOnlyDictionary<string, ValueRule> rules)
    : ISaConfigurationTransform
{
    public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
    {
        ITransformContextView? contextView = configuration as ITransformContextView;
        string? clientId = contextView?.ClientId;

        foreach (KeyValuePair<string, string?> pair in configuration.Values)
        {
            if (!rules.TryGetValue(pair.Key, out ValueRule rule))
                continue;

            bool coreFallback = contextView?.IsCoreFallback(pair.Key) ?? false;

            if (coreFallback && !rule.HandlesCoreFallback)
                continue;

            ValueTransformContext context = new(pair.Key, clientId, coreFallback);
            string? changed = rule.Map(context, pair.Value);

            // Unchanged values stay out of the pass data: in the core they would shadow the source
            // with a copy, and in a client overlay they would freeze a copy of the core value.
            if (string.Equals(pair.Value, changed, StringComparison.Ordinal))
                continue;

            yield return new KeyValuePair<string, string?>(pair.Key, changed);
        }
    }
}
