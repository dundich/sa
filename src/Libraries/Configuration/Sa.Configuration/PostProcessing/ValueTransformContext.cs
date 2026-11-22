namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Context of one per-key value rule: which key is being mapped and which configuration layer the
/// rule is running over.
/// </summary>
/// <remarks>
/// <see cref="ClientId"/> is <see langword="null"/> for the root configuration and carries the
/// client id for a client overlay. <see cref="IsCoreFallback"/> is <see langword="true"/> when an
/// earlier transform derived the value from the core configuration rather than from the layer's own
/// rows (a key that merely falls back to the core is not offered to any rule); the simple
/// <see cref="SaConfigurationValueTransformBuilder.Add(string, System.Func{string?, string?})"/>
/// overload skips such values, while the context overload receives them and can decide.
/// </remarks>
public readonly record struct ValueTransformContext
{
    internal ValueTransformContext(string key, string? clientId, bool isCoreFallback)
    {
        Key = key;
        ClientId = clientId;
        IsCoreFallback = isCoreFallback;
    }

    /// <summary>The key being mapped, in the canonical <c>:</c> spelling.</summary>
    public string Key { get; }

    /// <summary>
    /// Client id when the rule runs over a client overlay; <see langword="null"/> for the root
    /// configuration.
    /// </summary>
    public string? ClientId { get; }

    /// <summary>
    /// <see langword="true"/> when the value came from the core configuration fallback rather than
    /// from the layer's own rows. A core-derived value has already been transformed by the core
    /// pass, so the simple per-key overload does not run over it.
    /// </summary>
    public bool IsCoreFallback { get; }
}
