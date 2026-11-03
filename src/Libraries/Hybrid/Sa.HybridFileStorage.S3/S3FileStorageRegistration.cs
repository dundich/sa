namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Per-registration handle: records the registration's options-instance name — the key its named
/// options and its keyed bucket client share. Tests resolve the named instance and the client by
/// reading this name back, never by hardcoding it.
/// </summary>
internal sealed class S3FileStorageRegistration(string optionsName)
{
    /// <summary>The name of this registration's named options instance.</summary>
    public string OptionsName { get; } =
        optionsName ?? throw new ArgumentNullException(nameof(optionsName));
}
