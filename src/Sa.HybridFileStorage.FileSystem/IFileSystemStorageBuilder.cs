using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Collects the configuration of the filesystem provider registered through
/// <see cref="Setup.AddSaFileSystemFileStorage"/>: the standard options pipeline
/// (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) and the configuration
/// section, in one registration delegate.
/// </summary>
/// <remarks>
/// Both configuring intents live here — <see cref="FromConfiguration"/> for the section
/// and <see cref="Options"/> for the pipeline — so <c>AddSaFileSystemFileStorage</c> takes
/// a single <c>configure</c> parameter. The section binds in the fixed slot before the
/// <see cref="Options"/> actions, so a <c>Configure</c> there always wins over
/// configuration, wherever these calls sit in the callback.
/// </remarks>
public interface IFileSystemStorageBuilder
{
    /// <summary>
    /// Binds <see cref="FileSystemStorageOptions"/> from the given configuration section,
    /// e.g. <c>"FileSystemStorage"</c> — the section otherwise passed as
    /// <c>AddSaFileSystemFileStorage</c>'s <c>configSectionPath</c> argument, moved into
    /// the one registration delegate.
    /// </summary>
    /// <remarks>
    /// Recorded on the builder; <c>AddSaFileSystemFileStorage</c> binds it in the fixed
    /// slot before the <see cref="Options"/> actions — so an <see cref="Options"/>
    /// <c>Configure</c> always wins over configuration, no matter where this call sits in
    /// the callback. May be called several times; the last path wins.
    /// </remarks>
    /// <param name="configSectionPath">Configuration section path, e.g. <c>"FileSystemStorage"</c>.</param>
    IFileSystemStorageBuilder FromConfiguration(string configSectionPath);

    /// <summary>
    /// Hands the standard <see cref="OptionsBuilder{TOptions}"/> for
    /// <see cref="FileSystemStorageOptions"/> to the callback — the <c>Configure</c> /
    /// <c>PostConfigure</c> / <c>Validate</c> surface of the options pipeline, in one
    /// registration channel next to the section binding.
    /// </summary>
    /// <remarks>
    /// Runs in the options pipeline <b>after</b> the <c>BindConfiguration</c> call made for
    /// <see cref="FromConfiguration"/>, so a <c>Configure</c> here is the settings-level
    /// escape hatch that beats configuration, while a <c>Validate</c> adds to — rather than
    /// replaces — the built-in checks. May be called several times; the actions run in
    /// call order.
    /// </remarks>
    /// <param name="configureSettings">Callback receiving the settings options builder.</param>
    IFileSystemStorageBuilder Options(Action<OptionsBuilder<FileSystemStorageOptions>> configureSettings);
}
