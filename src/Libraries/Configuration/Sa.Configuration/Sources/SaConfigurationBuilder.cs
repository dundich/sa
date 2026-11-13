using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Sa.Configuration.CommandLine;
using Sa.Configuration.SecretStore;

namespace Sa.Configuration.Sources;

/// <summary>
/// Composition of the standard static sources: <c>appsettings.json</c>,
/// <c>appsettings.{environment}.json</c>, environment variables and (optionally) command-line
/// arguments.
/// </summary>
/// <remarks>
/// In an ASP.NET Core or generic-host application these sources are already registered by the host,
/// in exactly this order, so <c>UseStandardSources</c> detects them and adds nothing. It exists for
/// console tools, tests and hosts whose defaults were replaced.
/// </remarks>
public sealed class StandardConfigurationSourcesOptions
{
    /// <summary>Base directory for the JSON files. <see langword="null"/> keeps the builder's current one.</summary>
    public string? BasePath { get; set; }

    /// <summary>Whether a missing JSON file is tolerated. Default is <see langword="true"/>.</summary>
    public bool Optional { get; set; } = true;

    /// <summary>Whether a changed JSON file is picked up without a restart. Default is <see langword="true"/>.</summary>
    public bool ReloadOnChange { get; set; } = true;

    /// <summary>Adds <c>AddEnvironmentVariables()</c> with no prefix. Default is <see langword="true"/>.</summary>
    public bool IncludeEnvironmentVariables { get; set; } = true;

    /// <summary>
    /// Adds the Sa command-line parser (<c>--key value</c>, <c>--key=value</c>, <c>-flag</c>).
    /// Default is <see langword="true"/>; an existing source is not added twice.
    /// </summary>
    public bool IncludeCommandLineArguments { get; set; } = true;

    /// <summary>
    /// Environment name used for the <c>{environment}</c> token. Defaults to
    /// <c>DOTNET_ENVIRONMENT</c> → <c>ASPNETCORE_ENVIRONMENT</c> → <c>environment</c> →
    /// <c>Production</c>.
    /// </summary>
    public string EnvironmentName { get; set; } = Secrets.GetEnvironmentName();

    /// <summary>
    /// JSON files to add, in priority order. <c>{environment}</c> is replaced with
    /// <see cref="EnvironmentName"/>.
    /// </summary>
    public string[] JsonFiles { get; set; } =
    [
        "appsettings.json",
        "appsettings.{environment}.json"
    ];
}

/// <summary>
/// Fluent façade over <see cref="IConfigurationBuilder"/> that assembles the layers in a fixed
/// order: static sources first, dynamic sources after them (so they override the base values), the
/// secret/alias pass and the clients last.
/// </summary>
/// <remarks>
/// Every method re-appends the post-processing pass, so the aliases keep winning over the sources
/// registered through the builder — no manual <c>FinalizeSaConfiguration()</c> is needed as long as
/// sources are added through this façade. Use <see cref="Finalize"/> when plain
/// <see cref="IConfigurationBuilder"/> sources are added afterwards.
/// </remarks>
public sealed class SaConfigurationBuilder
{
    private readonly IConfigurationBuilder _builder;
    private readonly PostProcessing.SaConfigurationState _state;

    private SaConfigurationBuilder(
        IConfigurationBuilder builder,
        PostProcessing.SaConfigurationState state,
        SaConfigurationRuntime runtime)
    {
        _builder = builder;
        _state = state;
        Runtime = runtime;
    }

    /// <summary>The runtime that tracks the dynamic providers of this configuration.</summary>
    public SaConfigurationRuntime Runtime { get; }

    /// <summary>The wrapped builder, for the few sources that have no façade method.</summary>
    public IConfigurationBuilder Builder => _builder;

    /// <summary>
    /// Returns the façade for <paramref name="builder"/>, creating the runtime on first use.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static SaConfigurationBuilder Create(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return new SaConfigurationBuilder(
            builder,
            PostProcessing.SaConfigurationState.GetOrAdd(builder),
            SaConfigurationRuntime.GetOrAdd(builder));
    }

    /// <summary>
    /// Adds a static source. Later sources override earlier ones.
    /// </summary>
    public SaConfigurationBuilder Use(IConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _builder.Add(source);
        MovePassLast();

        return this;
    }

    /// <summary>
    /// Adds a source and records the layer it belongs to.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="layer"/> is <see cref="SaConfigurationLayer.Dynamic"/> and
    /// <paramref name="source"/> is not an <see cref="ISaDynamicConfigurationSource"/>.
    /// </exception>
    public SaConfigurationBuilder Use(IConfigurationSource source, SaConfigurationLayer layer)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (layer == SaConfigurationLayer.Dynamic && source is not ISaDynamicConfigurationSource)
        {
            throw new ArgumentException(
                "A dynamic source must implement ISaDynamicConfigurationSource so the reloader can find it.",
                nameof(source));
        }

        return Use(source);
    }

    /// <summary>
    /// Adds a source whose values can change at runtime. It is placed after the static sources, so
    /// it overrides them, and only it is reloaded by
    /// <see cref="ISaConfigurationReloader.ReloadDynamicAsync"/>.
    /// </summary>
    public SaConfigurationBuilder UseDynamic(ISaDynamicConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Use(source);
    }

    /// <summary>
    /// Adds the standard static set unless the host already did: <c>appsettings.json</c>,
    /// <c>appsettings.{environment}.json</c>, environment variables and command-line arguments.
    /// </summary>
    /// <remarks>
    /// Idempotent: a JSON file, environment-variable provider or argument source that is already
    /// registered is left alone. In ASP.NET Core this method is a no-op.
    /// </remarks>
    public SaConfigurationBuilder UseStandardSources(Action<StandardConfigurationSourcesOptions>? configure = null)
    {
        StandardConfigurationSourcesOptions options = new();
        configure?.Invoke(options);

        if (options.BasePath is { Length: > 0 } basePath)
            _builder.SetBasePath(basePath);

        foreach (string template in options.JsonFiles)
        {
            string path = template.Replace("{environment}", options.EnvironmentName, StringComparison.OrdinalIgnoreCase);

            if (HasJsonSource(path))
                continue;

            _builder.AddJsonFile(path, options.Optional, options.ReloadOnChange);
        }

        if (options.IncludeEnvironmentVariables && !HasEnvironmentSource())
            _builder.AddEnvironmentVariables();

        if (options.IncludeCommandLineArguments && !HasArgumentsSource())
            _builder.AddSaCommandLine(Environment.GetCommandLineArgs());

        MovePassLast();

        return this;
    }

    /// <summary>
    /// Adds the secret pipeline: <c>{{placeholder}}</c> substitution, key aliases and the
    /// command-line source. Repeated calls accumulate.
    /// </summary>
    public SaConfigurationBuilder UseSecrets(SecretOptions? options = null)
    {
        _builder.AddSaConfiguration(options);
        return this;
    }

    /// <summary>
    /// Moves the post-processing pass after every registered source and returns the wrapped
    /// builder.
    /// </summary>
    /// <remarks>Call it last when sources are added to <see cref="Builder"/> directly.</remarks>
    public IConfigurationBuilder Finalize()
    {
        MovePassLast();
        return _builder;
    }

    private void MovePassLast() => PostProcessing.PostProcessingPipeline.MoveToEnd(_builder, _state);

    private bool HasJsonSource(string path)
        => _builder.Sources
            .OfType<JsonConfigurationSource>()
            .Any(source => string.Equals(source.Path, path, StringComparison.OrdinalIgnoreCase));

    private bool HasEnvironmentSource()
        => _builder.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Any(source => string.IsNullOrEmpty(source.Prefix));

    private bool HasArgumentsSource()
        => _builder.Sources.OfType<ArgumentsConfigurationSource>().Any();
}
