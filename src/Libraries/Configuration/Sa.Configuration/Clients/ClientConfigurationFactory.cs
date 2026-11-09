using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Configuration.Clients;

/// <summary>
/// Creates <see cref="IConfiguration"/> views for client names known at runtime.
/// </summary>
/// <remarks>
/// Keyed dependency injection (<c>[FromKeyedServices("acme")]</c>) needs the client name at compile
/// time. A factory takes the name from the request, a job or a database row instead and caches the
/// (stateless, live) view per client. Clients are discovered from the overrides themselves, so a
/// client added to the database appears after a reload without a restart.
/// </remarks>
public interface IClientConfigurationFactory
{
    /// <summary>Section the client overrides live in.</summary>
    string SectionPath { get; }

    /// <summary>
    /// Clients declared in the configuration. Recomputed on every access; a client without any
    /// overrides is not listed.
    /// </summary>
    IReadOnlyList<string> Clients { get; }

    /// <summary>
    /// Returns the view of <paramref name="client"/>. For a client without overrides the view
    /// reads the base configuration — it never throws.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="client"/> is blank.</exception>
    IConfiguration For(string client);

    /// <summary>
    /// Resolves the view of <paramref name="client"/> and reports whether the configuration
    /// actually declares that client.
    /// </summary>
    bool TryFor(string client, out IConfiguration configuration);
}

/// <summary>
/// Default <see cref="IClientConfigurationFactory"/>: discovers clients from the overrides section
/// and caches one live view per client name.
/// </summary>
public sealed class ClientConfigurationFactory : IClientConfigurationFactory
{
    private readonly IConfiguration _configuration;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IConfiguration> _views =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the factory over the assembled configuration.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    public ClientConfigurationFactory(IConfiguration configuration, string sectionPath = ClientConfiguration.DefaultSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        _configuration = configuration;
        SectionPath = sectionPath;
    }

    /// <inheritdoc/>
    public string SectionPath { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> Clients
    {
        get
        {
            string section = SectionPath;

            return
            [
                .. _configuration.GetSection(section)
                    .GetChildren()
                    .Select(child => child.Key)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
            ];
        }
    }

    /// <inheritdoc/>
    public IConfiguration For(string client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);

        // The cached view is a thin, live wrapper: only the prefix is cached, never a value.
        return _views.GetOrAdd(client, name => new ClientConfiguration(_configuration, name, SectionPath));
    }

    /// <inheritdoc/>
    public bool TryFor(string client, out IConfiguration configuration)
    {
        configuration = For(client);

        return _configuration
            .GetSection(SectionPath)
            .GetSection(client)
            .Exists();
    }
}

/// <summary>
/// Helpers for the per-client overlay.
/// </summary>
public static class ClientConfigurationExtensions
{
    /// <summary>
    /// Returns the view of <paramref name="client"/> over <paramref name="configuration"/>.
    /// </summary>
    public static IConfiguration ForClient(
        this IConfiguration configuration,
        string client,
        string sectionPath = ClientConfiguration.DefaultSectionPath)
        => new ClientConfiguration(configuration, client, sectionPath);

    /// <summary>
    /// Returns the client names declared under <paramref name="sectionPath"/> (default
    /// <c>sa:clients</c>).
    /// </summary>
    public static IReadOnlyList<string> GetClientNames(
        this IConfiguration configuration,
        string sectionPath = ClientConfiguration.DefaultSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return
        [
            .. configuration.GetSection(sectionPath)
                .GetChildren()
                .Select(child => child.Key)
                .Where(key => !string.IsNullOrWhiteSpace(key))
        ];
    }

    /// <summary>
    /// Registers <see cref="IClientConfigurationFactory"/> and a keyed
    /// <see cref="IConfiguration"/> resolvable with any client name.
    /// </summary>
    /// <remarks>
    /// The keyed registration uses <see cref="KeyedService.AnyKey"/>, so
    /// <c>GetRequiredKeyedService&lt;IConfiguration&gt;(clientId)</c> works for a name that only
    /// exists at runtime. Enumerating <c>IEnumerable&lt;IConfiguration&gt;</c> does not enumerate
    /// clients — use <see cref="IClientConfigurationFactory.Clients"/> for that.
    /// </remarks>
    public static IServiceCollection AddSaClientConfiguration(
        this IServiceCollection services,
        string sectionPath = ClientConfiguration.DefaultSectionPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        services.TryAddSingleton<IClientConfigurationFactory>(sp =>
            new ClientConfigurationFactory(sp.GetRequiredService<IConfiguration>(), sectionPath));

        services.TryAddKeyedSingleton<IConfiguration>(
            KeyedService.AnyKey,
            (sp, key) => sp.GetRequiredService<IClientConfigurationFactory>().For((string)key!));

        return services;
    }
}
