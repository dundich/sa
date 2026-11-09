using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Sa.Configuration.Clients;

namespace Sa.Configuration;

/// <summary>
/// Registers the Sa configuration runtime in a service collection.
/// </summary>
public static class SaConfigurationRuntimeSetup
{
    /// <summary>
    /// Registers <see cref="ISaConfigurationReloader"/> for the configuration assembled on
    /// <paramref name="configuration"/>.
    /// </summary>
    /// <remarks>
    /// By default nothing runs in the background: the service is driven by the application
    /// (<c>EnsureInitializedAsync</c> after <c>builder.Build()</c>, and <c>ReloadDynamicAsync</c>
    /// wherever a refresh is wanted). A periodic loop is registered only when
    /// <see cref="SaConfigurationRuntimeOptions.DynamicReload"/> is switched on, and a one-shot
    /// start-up hook only when <see cref="SaConfigurationRuntimeOptions.InitialLoad"/> asks for it.
    /// <para>
    /// The late-bound source strategy (<c>PgConfigurationConnection.FromServices</c>) needs this
    /// registration: the data source is resolved from the container during
    /// <c>EnsureInitializedAsync</c>, never earlier.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="configuration"/> never went through a Sa registration.
    /// </exception>
    public static IServiceCollection AddSaConfigurationRuntime(
        this IServiceCollection services,
        IConfigurationBuilder configuration,
        Action<SaConfigurationRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        SaConfigurationRuntime runtime = SaConfigurationRuntime.Find(configuration)
            ?? throw new InvalidOperationException(
                "The configuration builder has no Sa runtime. Call "
                + "builder.Configuration.AddSaConfiguration(sa => ...) before registering the runtime.");

        SaConfigurationRuntimeOptions options = new();
        configure?.Invoke(options);

        if (!string.IsNullOrWhiteSpace(options.ClientsSectionPath))
            runtime.ClientsSectionPath = options.ClientsSectionPath;

        services.TryAddSingleton(runtime);
        services.TryAddSingleton<ISaConfigurationReloader>(sp =>
        {
            runtime.AttachServices(sp);
            return runtime;
        });

        if (options.KeyedClientConfigurations)
            services.AddSaClientConfiguration(runtime.ClientsSectionPath);

        if (options.DynamicReload || options.InitialLoad != SaDynamicInitialLoad.Explicit)
            services.AddSingleton<IHostedService>(sp => new SaConfigurationHostedService(sp, options));

        return services;
    }

    /// <summary>
    /// Registers the runtime and a key-addressable <see cref="IConfiguration"/> per client.
    /// </summary>
    /// <remarks>
    /// Shorthand for <c>AddSaConfigurationRuntime(..., o => o.KeyedClientConfigurations = true)</c>.
    /// </remarks>
    public static IServiceCollection AddSaConfigurationRuntimeWithClients(
        this IServiceCollection services,
        IConfigurationBuilder configuration,
        Action<SaConfigurationRuntimeOptions>? configure = null)
        => services.AddSaConfigurationRuntime(configuration, options =>
        {
            options.KeyedClientConfigurations = true;
            configure?.Invoke(options);
        });
}
