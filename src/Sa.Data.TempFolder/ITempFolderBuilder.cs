using Microsoft.Extensions.Options;
using Sa.Data.TempFolder.Cleanup;
using Sa.Data.TempFolder.Naming;

namespace Sa.Data.TempFolder;

/// <summary>
/// Collects the configuration of one temp-folder instance registered through
/// <see cref="Setup.AddSaTempFolder"/>: the configuration section, the standard options pipeline
/// (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) and optional code overrides for the
/// cleanup / naming strategies, in one registration delegate.
/// </summary>
public interface ITempFolderBuilder
{
    /// <summary>
    /// Binds <see cref="TempFolderOptions"/> from the given configuration section, e.g.
    /// <c>"TempFolder:Uploads"</c>. Recorded and bound by <see cref="Setup.AddSaTempFolder"/> in
    /// the fixed slot before the <see cref="Options"/> actions — so an <c>Configure</c> there
    /// always wins over configuration. The last path wins.
    /// </summary>
    ITempFolderBuilder FromConfiguration(string configSectionPath);

    /// <summary>
    /// Hands the standard <see cref="OptionsBuilder{TOptions}"/> to the callback — the
    /// <c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c> surface of the options pipeline,
    /// in one channel next to the section binding. May be called several times; the actions run in
    /// call order after the section binding and the registration's own normalisation.
    /// </summary>
    ITempFolderBuilder Options(Action<OptionsBuilder<TempFolderOptions>> configureSettings);

    /// <summary>
    /// Overrides the cleanup strategy for this instance. The type is registered as a singleton
    /// (dependencies resolve from the container) and wins over
    /// <see cref="TempFolderOptions.Cleanup"/> from configuration.
    /// </summary>
    ITempFolderBuilder UseCleanupStrategy<T>() where T : class, ICleanupStrategy;

    /// <summary>
    /// Overrides the folder-naming strategy for this instance. The type is registered as a
    /// singleton (dependencies resolve from the container) and wins over
    /// <see cref="TempFolderOptions.Naming"/> from configuration.
    /// </summary>
    ITempFolderBuilder UseNamingStrategy<T>() where T : class, IFolderNameStrategy;
}
