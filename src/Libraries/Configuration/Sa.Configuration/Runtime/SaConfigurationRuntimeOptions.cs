namespace Sa.Configuration;

/// <summary>
/// When the first load of the dynamic layers happens.
/// </summary>
public enum SaDynamicInitialLoad
{
    /// <summary>
    /// No automatic load. The application calls
    /// <see cref="ISaConfigurationReloader.EnsureInitializedAsync"/> itself (typically right after
    /// <c>builder.Build()</c>). This is the default: no hosted service is registered.
    /// </summary>
    Explicit,

    /// <summary>
    /// A one-shot hosted hook loads the dynamic layers before the server starts accepting requests.
    /// A failure fails the host start.
    /// </summary>
    Hosted,

    /// <summary>
    /// The host starts immediately and the dynamic layers are loaded in the background. Useful when
    /// the database must not block startup; a failure is logged, not thrown.
    /// </summary>
    Background
}

/// <summary>
/// Options for <c>AddSaConfigurationRuntime</c>.
/// </summary>
public sealed class SaConfigurationRuntimeOptions
{
    /// <summary>
    /// Section that holds per-client overrides. Defaults to <see cref="Clients.ClientConfiguration.DefaultSectionPath"/>.
    /// </summary>
    public string ClientsSectionPath { get; set; } = Clients.ClientConfiguration.DefaultSectionPath;

    /// <summary>
    /// Registers a keyed <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> per client
    /// (resolvable with any key) plus <see cref="Clients.IClientConfigurationFactory"/>.
    /// </summary>
    public bool KeyedClientConfigurations { get; set; }

    /// <summary>
    /// When to perform the first dynamic load. Defaults to <see cref="SaDynamicInitialLoad.Explicit"/>.
    /// </summary>
    public SaDynamicInitialLoad InitialLoad { get; set; } = SaDynamicInitialLoad.Explicit;

    /// <summary>
    /// Enables periodic reloading of the dynamic layers. Off by default — no timer, no background
    /// work is registered unless this is switched on.
    /// </summary>
    public bool DynamicReload { get; set; }

    /// <summary>Period of the periodic reload. Defaults to 30 seconds.</summary>
    public TimeSpan DynamicReloadInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay before the first periodic reload. <see langword="null"/> means one full interval.</summary>
    public TimeSpan? DynamicReloadInitialDelay { get; set; }
}
