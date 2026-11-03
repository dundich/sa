namespace Sa.Data.TempFolder.Naming;

/// <summary>
/// Creates the folder name for <c>CreateSubfolder()</c> when the caller does not pass one.
/// Selected per registration — either the built-in strategy named by
/// <see cref="TempFolderOptions.Naming"/> or a code override registered with
/// <c>UseNamingStrategy&lt;T&gt;()</c>.
/// </summary>
public interface IFolderNameStrategy
{
    /// <summary>
    /// Returns a fresh folder name (a single path segment) for the given options. The instance
    /// resolves the result against the root through <see cref="PathGuard"/>, so a strategy cannot
    /// escape the root either.
    /// </summary>
    /// <param name="options">The instance's normalised options snapshot.</param>
    string CreateFolderName(TempFolderOptions options);
}
