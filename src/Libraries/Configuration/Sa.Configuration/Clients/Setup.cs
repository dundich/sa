using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Configuration.Clients;

/// <summary>
/// Collects client providers and the registry options before the service collection is built.
/// </summary>
/// <remarks>
/// A provider is a callback with one to three dependencies resolved from the container (plus the
/// cancellation token) that returns flat <see cref="ClientSetting"/> rows for every client. The
/// registry groups the rows by <see cref="ClientSetting.ClientId"/> and builds one overlay per
/// client.
/// <para>
/// The section-based variant is <see cref="ConfigurationClientSetup.UseConfiguration"/>, which is
/// the same provider callback reading a configuration section instead of a database.
/// </para>
/// </remarks>
public sealed class SaClientConfigurationRegistration
{
    private readonly List<RowLoader> _loaders = [];
    private readonly ClientConfigurationOptions _options = new();

    /// <summary>
    /// Registers a provider whose single dependency <typeparamref name="T1"/> is resolved from
    /// dependency injection when the data is read.
    /// </summary>
    /// <example>
    /// <code>
    /// clients.Add&lt;IClientSettingsStore&gt;((store, ct) => store.LoadAllAsync(ct));
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="load"/> is null.</exception>
    public SaClientConfigurationRegistration Add<T1>(
        Func<T1, CancellationToken, ValueTask<IReadOnlyList<ClientSetting>>> load)
        where T1 : notnull
    {
        ArgumentNullException.ThrowIfNull(load);

        _loaders.Add((services, cancellationToken) => load(services.GetRequiredService<T1>(), cancellationToken));
        return this;
    }

    /// <summary>
    /// Registers a provider whose two dependencies are resolved from dependency injection when the
    /// data is read.
    /// </summary>
    /// <example>
    /// <code>
    /// clients.Add&lt;IClientSettingsStore, ILogger&lt;Settings&gt;&gt;(
    ///     (store, logger, ct) => store.LoadAllAsync(logger, ct));
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="load"/> is null.</exception>
    public SaClientConfigurationRegistration Add<T1, T2>(
        Func<T1, T2, CancellationToken, ValueTask<IReadOnlyList<ClientSetting>>> load)
        where T1 : notnull
        where T2 : notnull
    {
        ArgumentNullException.ThrowIfNull(load);

        _loaders.Add((services, cancellationToken) => load(
            services.GetRequiredService<T1>(),
            services.GetRequiredService<T2>(),
            cancellationToken));

        return this;
    }

    /// <summary>
    /// Registers a provider whose three dependencies are resolved from dependency injection when
    /// the data is read.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="load"/> is null.</exception>
    public SaClientConfigurationRegistration Add<T1, T2, T3>(
        Func<T1, T2, T3, CancellationToken, ValueTask<IReadOnlyList<ClientSetting>>> load)
        where T1 : notnull
        where T2 : notnull
        where T3 : notnull
    {
        ArgumentNullException.ThrowIfNull(load);

        _loaders.Add((services, cancellationToken) => load(
            services.GetRequiredService<T1>(),
            services.GetRequiredService<T2>(),
            services.GetRequiredService<T3>(),
            cancellationToken));

        return this;
    }

    /// <summary>Adjusts the registry behavior.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public SaClientConfigurationRegistration Configure(Action<ClientConfigurationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(_options);
        return this;
    }

    /// <summary>
    /// Section whose subtree is meta-data of the overlay and is hidden from every client view, set
    /// by <see cref="ConfigurationClientSetup.UseConfiguration"/>. The last call wins.
    /// </summary>
    internal string? HiddenSectionPath { get; set; }

    internal SaClientConfigurationRegistration AddLoader(RowLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        _loaders.Add(loader);
        return this;
    }

    internal ClientConfigurations Build(IConfiguration core, IServiceProvider services)
    {
        ClientConfigurations clients = new(core, _options, HiddenSectionPath);
        clients.SetServices(services);

        foreach (RowLoader loader in _loaders)
            clients.AddLoader(loader);

        return clients;
    }
}

/// <summary>
/// Registers the per-client configuration registry.
/// </summary>
public static class ClientConfigurationSetup
{
    /// <summary>
    /// Registers <see cref="IClientConfigurations"/>, a keyed <see cref="IConfiguration"/> per
    /// client and the providers declared in <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// The keyed registration uses <see cref="KeyedService.AnyKey"/>, so
    /// <c>GetRequiredKeyedService&lt;IConfiguration&gt;(clientId)</c> works for a name that only
    /// exists at runtime. Enumerating keyed services does not enumerate clients — use
    /// <see cref="IClientConfigurations.Clients"/> for that.
    /// <para>
    /// When <paramref name="configuration"/> is also an <see cref="IConfigurationBuilder"/>
    /// (an <c>IConfigurationManager</c>), the registry is attached to the Sa runtime, so
    /// <see cref="ISaConfigurationReloader.ReloadDynamicAsync"/> also refreshes the client overlays
    /// and re-reads the providers.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or
    /// <paramref name="configuration"/> is null.</exception>
    public static IServiceCollection AddSaClientConfigurations(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SaClientConfigurationRegistration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        SaClientConfigurationRegistration registration = new();
        configure?.Invoke(registration);

        services.TryAddSingleton<IClientConfigurations>(sp =>
        {
            ClientConfigurations clients = registration.Build(configuration, sp);

            if (configuration is IConfigurationBuilder builder
                && SaConfigurationRuntime.Find(builder) is { } runtime)
            {
                runtime.AttachClientConfigurations(clients);
            }

            return clients;
        });

        services.TryAddKeyedSingleton<IConfiguration>(
            KeyedService.AnyKey,
            (sp, key) => sp.GetRequiredService<IClientConfigurations>().GetClient((string)key!));

        return services;
    }
}
