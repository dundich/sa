using Sa.Data.TempFolder;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Per-registration handle, keyed by the registration name: records the registration's options-instance
/// name — the handle tests resolve the named instance by. The keyed <see cref="ITempFolder"/> shares
/// the same key.
/// </summary>
internal sealed class FileSystemStorageRegistration(string name, string optionsName)
{
    /// <summary>The keyed-service key of the registration.</summary>
    public string Name { get; } =
        name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The name of this registration's named options instance.</summary>
    public string OptionsName { get; } =
        optionsName ?? throw new ArgumentNullException(nameof(optionsName));
}
