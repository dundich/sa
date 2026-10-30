using Microsoft.Extensions.Configuration;
using Sa.Configuration.CommandLine;
using Sa.Configuration.SecretStore;

namespace Sa.Configuration.PostProcessing;

/// <summary>
/// Everything the final configuration pass needs, accumulated on the builder itself so repeated
/// registrations add up instead of installing independent passes.
/// </summary>
/// <remarks>
/// The state lives in <see cref="IConfigurationBuilder.Properties"/> under
/// <see cref="PropertyKey"/>. Because the whole configuration hierarchy is built from a single
/// builder, every registration path (any extension that receives the builder) reaches the same
/// instance.
/// </remarks>
internal sealed class SaConfigurationState
{
    /// <summary>Key the state is stored under in the builder's property bag.</summary>
    public const string PropertyKey = "Sa.Configuration.State";

    /// <summary>Union of every registered mapping, canonical <c>:</c> spelling, normalized alias.</summary>
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a non-blank canonical value protects the path from the alias.</summary>
    public bool CanonicalValueWins { get; private set; }

    /// <summary>Whether the pass substitutes <c>{{secret}}</c> placeholders at all.</summary>
    public bool SubstituteSecrets { get; private set; }

    /// <summary>Secret options from the most recent registration.</summary>
    public SecretOptions Options { get; private set; } = new();

    /// <summary>Command-line arguments from the most recent registration that supplied any.</summary>
    public string[]? Args { get; private set; }

    /// <summary>Custom transforms, applied after the built-in ones in registration order.</summary>
    public List<ISaConfigurationTransform> Transforms { get; } = [];

    /// <summary>The single command-line source this configuration owns, if any.</summary>
    public ArgumentsConfigurationSource? CommandLineSource { get; set; }

    /// <summary>The single post-processing source this configuration owns, if any.</summary>
    public SaPostProcessingConfigurationSource? PostProcessingSource { get; set; }

    private Lazy<ISecretService>? _secrets;

    /// <summary>
    /// Returns the state stored on <paramref name="builder"/>, creating it on first use.
    /// </summary>
    public static SaConfigurationState GetOrAdd(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Properties.TryGetValue(PropertyKey, out object? value)
            && value is SaConfigurationState existing)
        {
            return existing;
        }

        SaConfigurationState created = new();
        builder.Properties[PropertyKey] = created;
        return created;
    }

    /// <summary>
    /// Applies a registration: merges its aliases into the accumulated map and makes its secret
    /// options the ones the single pass will use (the last registration wins).
    /// </summary>
    /// <exception cref="ArgumentException">A mapping key or alias is blank.</exception>
    public void Configure(SecretOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        AddAliases(options.KeyAliases);

        Options = options;
        SubstituteSecrets = true;
        _secrets = null;

        if (options.Args is not null)
            Args = options.Args;
    }

    /// <summary>
    /// Merges the mappings of one registration. A canonical path already present is overwritten,
    /// so the last registration wins for a conflicting mapping.
    /// </summary>
    /// <exception cref="ArgumentException">A mapping key or alias is blank.</exception>
    public void AddAliases(KeyAliasOptions keyAliasOptions)
    {
        ArgumentNullException.ThrowIfNull(keyAliasOptions);

        keyAliasOptions.Validate();

        foreach (KeyValuePair<string, string> mapping in keyAliasOptions.KeyAliases)
        {
            Aliases[mapping.Key] = mapping.Value;
        }

        CanonicalValueWins = keyAliasOptions.CanonicalValueWins;
    }

    /// <summary>
    /// Uses an externally supplied secret service — the low-level
    /// <c>AddSaPostSecretProcessing</c> path.
    /// </summary>
    public void UseSecretService(ISecretService secretService)
    {
        ArgumentNullException.ThrowIfNull(secretService);

        SubstituteSecrets = true;
        _secrets = new Lazy<ISecretService>(() => secretService);
    }

    /// <summary>
    /// Returns the secret service for the current options, creating it on the first load.
    /// Creating it lazily keeps registration side-effect free: no file is read and no decryption
    /// key is required until the configuration is actually built.
    /// </summary>
    public ISecretService GetSecrets()
    {
        _secrets ??= new Lazy<ISecretService>(() => Secrets.CreateDefault(Args is null ? Options : Options with { Args = Args }));
        return _secrets.Value;
    }

    /// <summary>
    /// Builds the transform list for one load: built-ins first (placeholders, then aliases over
    /// their substituted values), custom transforms after them.
    /// </summary>
    public IReadOnlyList<ISaConfigurationTransform> BuildTransforms()
    {
        List<ISaConfigurationTransform> transforms = [];

        if (SubstituteSecrets)
            transforms.Add(new SecretSubstitutionTransform(GetSecrets));

        if (Aliases.Count > 0)
            transforms.Add(new KeyAliasTransform(Aliases, CanonicalValueWins));

        transforms.AddRange(Transforms);
        return transforms;
    }
}
