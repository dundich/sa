using Microsoft.Extensions.Options;

namespace Sa.HybridFileStorage;

/// <summary>
/// Collects the configuration of the in-memory provider registered through
/// <see cref="Setup.AddSaInMemoryFileStorage"/>: the standard options pipeline
/// (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) and the configuration
/// section, in one registration delegate.
/// </summary>
/// <remarks>
/// Both configuring intents live here — <see cref="FromConfiguration"/> for the section
/// and <see cref="Options"/> for the pipeline — so the builder-taking
/// <c>AddSaInMemoryFileStorage</c> overload takes a single <c>configure</c> parameter.
/// The section binds in the fixed slot before the <see cref="Options"/> actions, so a
/// <c>Configure</c> there always wins over configuration, wherever these calls sit in
/// the callback.
/// <para>
/// Unlike the explicit-instance overloads, this channel materialises the options through
/// the standard pipeline under a unique named instance, so a configuration section and
/// code actions compose instead of one replacing the other.
/// </para>
/// </remarks>
public interface IInMemoryFileStorageBuilder
{
    /// <summary>
    /// Binds <see cref="InMemoryFileStorageOptions"/> from the given configuration section,
    /// e.g. <c>"InMemoryFileStorage"</c>. The section may contain <c>Basket</c>,
    /// <c>IsReadOnly</c> and <c>MaxSizeBytes</c>.
    /// </summary>
    /// <param name="configSectionPath">The configuration section path.</param>
    /// <returns>The same builder instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configSectionPath"/> is <c>null</c>.</exception>
    IInMemoryFileStorageBuilder FromConfiguration(string configSectionPath);

    /// <summary>
    /// Adds actions to the standard options pipeline for this registration's named instance.
    /// </summary>
    /// <param name="configureSettings">
    /// The pipeline actions: <c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>.
    /// A <c>Configure</c> here runs after the section binding and therefore beats it;
    /// a <c>Validate</c> adds to — rather than replaces — the built-in checks.
    /// </param>
    /// <returns>The same builder instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configureSettings"/> is <c>null</c>.</exception>
    IInMemoryFileStorageBuilder Options(Action<OptionsBuilder<InMemoryFileStorageOptions>> configureSettings);
}
