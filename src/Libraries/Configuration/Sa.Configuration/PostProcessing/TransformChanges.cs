namespace Sa.Configuration.PostProcessing;

/// <summary>
/// A transform change together with the provenance of the value it produced.
/// </summary>
/// <param name="Key">The changed key.</param>
/// <param name="Value">The new value; <see langword="null"/> blanks the key.</param>
/// <param name="IsCoreFallback">
/// <see langword="true"/> when the new value was read from the core configuration fallback of a
/// client overlay rather than from the client's own rows.
/// </param>
internal readonly record struct TransformChange(string Key, string? Value, bool IsCoreFallback);

/// <summary>
/// A transform that can tell the pipeline whether a produced value came from the layer's own rows
/// or from the core fallback. Built-in alias resolution uses this; a custom transform that does not
/// implement it is treated as producing owned values.
/// </summary>
internal interface IOriginTransform
{
    IEnumerable<TransformChange> ApplyWithOrigin(ISaConfigurationView configuration);
}

/// <summary>
/// Enumerates a transform's changes with their provenance, regardless of the transform form.
/// </summary>
internal static class TransformChanges
{
    public static IEnumerable<TransformChange> Apply(
        ISaConfigurationTransform transform,
        ISaConfigurationView view)
        => transform is IOriginTransform origin
            ? origin.ApplyWithOrigin(view)
            : transform.Apply(view).Select(static change => new TransformChange(change.Key, change.Value, false));
}

/// <summary>
/// Internal, provenance-aware view of a configuration layer. The root view owns every key; a client
/// view distinguishes its own rows from values that came from the core fallback.
/// </summary>
internal interface ITransformContextView
{
    /// <summary>The client id for a client overlay; <see langword="null"/> for the root.</summary>
    string? ClientId { get; }

    /// <summary>
    /// Looks a key up and reports whether the hit came from the core fallback rather than from the
    /// layer itself.
    /// </summary>
    bool TryGetValueWithOrigin(string key, out string? value, out bool fromCore);

    /// <summary>Whether the current value of <paramref name="key"/> came from the core fallback.</summary>
    bool IsCoreFallback(string key);

    /// <summary>Whether the layer owns <paramref name="key"/> rather than only falling back to the core.</summary>
    bool IsOwned(string key);

    /// <summary>Records the provenance of a value written by a transform.</summary>
    void SetOrigin(string key, bool fromCore);
}
