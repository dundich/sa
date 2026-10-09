using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sa.Data.TempFolder.Cleanup;
using Sa.Data.TempFolder.Naming;

namespace Sa.Data.TempFolder;

/// <summary>
/// Provides extension methods for registering temp-folder management with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>The registration key used by the overload that does not take a name.</summary>
    public const string DefaultName = "default";

    /// <summary>
    /// Registers a temp-folder instance under an explicit key, so one host can run several roots
    /// with different prefixes, ages, strategies and limits.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="name">
    /// The keyed-service key the instance is resolved by:
    /// <c>sp.GetRequiredKeyedService&lt;ITempFolder&gt;(name)</c>. Must be unique per registration.
    /// </param>
    /// <param name="configure">
    /// The configuration channel: the section via <see cref="ITempFolderBuilder.FromConfiguration"/>,
    /// the lowest-precedence defaults via <see cref="ITempFolderBuilder.Defaults"/>, and the standard
    /// pipeline via <see cref="ITempFolderBuilder.Options"/>, plus strategy
    /// overrides via <c>UseCleanupStrategy&lt;T&gt;()</c> / <c>UseNamingStrategy&lt;T&gt;()</c>.
    /// Invoked once, immediately; the <c>Options(...)</c> actions are replayed after this method's
    /// own registrations, so their <c>Configure</c> runs last and their <c>Validate</c> adds to —
    /// rather than replaces — the built-in checks.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">A temp folder with the same name is already registered.</exception>
    /// <remarks>
    /// <para>
    /// Each call gets its own named options instance (the name is an internal detail derived from
    /// the section path or the key plus a sequence number), so two calls never stack their
    /// <c>Configure</c> callbacks on one shared instance. Options are materialised lazily — an
    /// invalid configuration surfaces as <see cref="OptionsValidationException"/> when the instance
    /// is first resolved or at host start (<c>ValidateOnStart()</c>), never at registration.
    /// </para>
    /// <para>
    /// The pipeline order is the house order: the caller's <see cref="ITempFolderBuilder.Defaults"/>
    /// → section binding → the caller's <c>Configure</c>/<c>PostConfigure</c> → this method's
    /// normalisation (root to a full path, trimmed prefix) → validation, so validation sees
    /// normalised values and a configured value always beats a default.
    /// </para>
    /// <para>
    /// A <see cref="BackgroundService"/> performing access checks at start and the periodic
    /// cleanup / volume-scan loops is registered once per collection (<c>TryAdd</c>) and walks
    /// <b>all</b> registered instances. <see cref="TimeProvider"/> is registered as
    /// <see cref="TimeProvider.System"/> when absent — register a test clock before this call to
    /// virtualise the debounce and the loops.
    /// </para>
    /// <para>
    /// Resolving <see cref="ILogger{T}"/> is optional: when the collection has no logging, a null
    /// logger is used, so a bare <see cref="ServiceCollection"/> in a test works as-is.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaTempFolder(
        this IServiceCollection services,
        string name,
        Action<ITempFolderBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A registration name is required.", nameof(name));
        }

        if (services.Any(d => d.ServiceType == typeof(ITempFolder) && Equals(d.ServiceKey, name)))
        {
            throw new InvalidOperationException(
                $"A temp folder named '{name}' is already registered in this service collection. " +
                "Registration names must be unique — pick another name for the second instance.");
        }

        TempFolderBuilder? builder = null;

        if (configure is not null)
        {
            builder = new TempFolderBuilder();
            configure(builder);
        }

        // Fixed slot: the section binds after the callback has recorded it, before its
        // Options(...) actions replay — wherever those calls sit in the callback.
        var sectionPath = builder?.ConfigSectionPath;
        var optionsName = NextOptionsName(sectionPath ?? name);

        services.AddSingleton(new TempFolderRegistration(name, optionsName));

        var optionsBuilder = services.AddOptions<TempFolderOptions>(optionsName);

        // Lowest precedence: the caller's Defaults(...) seed the instance first, so a value in the
        // bound section (and any Options(...) Configure) overrides it.
        if (builder is { DefaultsActions.Count: > 0 })
        {
            foreach (var defaultsAction in builder.DefaultsActions)
            {
                optionsBuilder.Configure(defaultsAction);
            }
        }

        if (sectionPath is not null)
        {
            optionsBuilder.BindConfiguration(sectionPath);
        }

        // Normalisation before validation: the checks below and the strategies see a fully
        // resolved root and a clean prefix.
        optionsBuilder.PostConfigure(static options =>
        {
            if (!string.IsNullOrWhiteSpace(options.RootPath))
            {
                options.RootPath = Path.GetFullPath(options.RootPath.Trim());
            }

            options.FolderPrefix = options.FolderPrefix?.Trim() ?? string.Empty;
        });

        optionsBuilder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT. One stateless validator serves
        // every named instance.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<TempFolderOptions>, TempFolderOptionsValidator>());

        // Replayed in this slot — after the section binding and after this method's own
        // PostConfigure — so the caller's Configure beats the section and its Validate runs
        // after ours.
        if (builder is { SettingsActions.Count: > 0 })
        {
            foreach (var settingsAction in builder.SettingsActions)
            {
                settingsAction(optionsBuilder);
            }
        }

        RegisterStrategies(services, name, builder, optionsName);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        // One background service per collection; it walks every registered instance.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TempFolderCleanerHost>());

        var registrationName = name;
        var registrationOptionsName = optionsName;

        services.AddKeyedSingleton<ITempFolder>(name, (provider, _) =>
        {
            var loggerFactory = provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

            return new TempFolder(
                registrationName,
                provider.GetRequiredService<IOptionsMonitor<TempFolderOptions>>().Get(registrationOptionsName),
                provider.GetRequiredKeyedService<ICleanupStrategy>(registrationName),
                provider.GetRequiredKeyedService<IFolderNameStrategy>(registrationName),
                loggerFactory.CreateLogger($"Sa.Data.TempFolder.{registrationName}"),
                provider.GetService<TimeProvider>() ?? TimeProvider.System);
        });

        return services;
    }

    /// <summary>
    /// Registers a temp-folder instance under the <see cref="DefaultName"/> key:
    /// <c>sp.GetRequiredKeyedService&lt;ITempFolder&gt;(Setup.DefaultName)</c> (a bare
    /// <c>GetRequiredService&lt;ITempFolder&gt;()</c> does not resolve keyed services).
    /// </summary>
    /// <inheritdoc cref="AddSaTempFolder(IServiceCollection, string, Action{ITempFolderBuilder}?)" path="/param[@name='services'] | /param[@name='configure'] | /returns | /exception | /remarks" />
    public static IServiceCollection AddSaTempFolder(
        this IServiceCollection services,
        Action<ITempFolderBuilder>? configure = null)
        => services.AddSaTempFolder(DefaultName, configure);

    /// <summary>
    /// Binds the strategy overrides: the overridden type is registered as a plain singleton
    /// (its own dependencies resolve from the container) and mapped onto this instance's keyed
    /// strategy slot; without an override the built-in named by the instance's
    /// <see cref="TempFolderOptions.Naming"/> / <see cref="TempFolderOptions.Cleanup"/> enum is
    /// selected for the key — at resolve time, because the enum may come from configuration.
    /// The name-unique guard in <see cref="AddSaTempFolder"/> guarantees exactly one mapping per key.
    /// </summary>
    private static void RegisterStrategies(IServiceCollection services, string name, TempFolderBuilder? builder, string optionsName)
    {
        if (builder?.CleanupStrategyType is { } cleanupType)
        {
            services.TryAdd(ServiceDescriptor.Singleton(cleanupType, cleanupType));
            services.AddKeyedSingleton<ICleanupStrategy>(name,
                (provider, _) => (ICleanupStrategy)provider.GetRequiredService(cleanupType));
        }
        else
        {
            services.AddKeyedSingleton<ICleanupStrategy, AgeBasedCleanupStrategy>(name);
        }

        if (builder?.NamingStrategyType is { } namingType)
        {
            services.TryAdd(ServiceDescriptor.Singleton(namingType, namingType));
            services.AddKeyedSingleton<IFolderNameStrategy>(name,
                (provider, _) => (IFolderNameStrategy)provider.GetRequiredService(namingType));
        }
        else
        {
            var registeredOptionsName = optionsName;
            services.AddKeyedSingleton<IFolderNameStrategy>(name, (provider, _) =>
            {
                // IOptionsMonitor.Get runs validation, so the enum and the format are known-good
                // here; the switch then only picks the built-in.
                var options = provider.GetRequiredService<IOptionsMonitor<TempFolderOptions>>()
                    .Get(registeredOptionsName);

                IFolderNameStrategy strategy = options.Naming switch
                {
                    TempFolderNamingKind.Date => new DateFolderNameStrategy(
                        provider.GetService<TimeProvider>() ?? TimeProvider.System),
                    _ => new GuidV7FolderNameStrategy(),
                };

                return strategy;
            });
        }
    }

    /// <summary>
    /// Sequence for unique options-instance names within this assembly — one registration,
    /// one named instance (the name is an internal detail: tests read it back through the
    /// registration marker, never by hardcoding it).
    /// </summary>
    private static int s_optionsSequence;

    /// <summary>
    /// Names this registration's options instance: the section path when one was given
    /// (readable in diagnostics), the registration key otherwise, plus a sequence number that
    /// makes the name unique per registration.
    /// </summary>
    private static string NextOptionsName(string label)
        => $"{label}#{Interlocked.Increment(ref s_optionsSequence)}";
}

/// <summary>
/// Sentinel marker recording that a temp folder is registered in this collection: carries the
/// registration's key and options-instance name — the handles the background service and tests
/// resolve the instance by. Registered one per <see cref="Setup.AddSaTempFolder"/> call.
/// </summary>
internal sealed class TempFolderRegistration(string name, string optionsName)
{
    /// <summary>The keyed-service key of the instance.</summary>
    public string Name { get; } =
        name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The name of this registration's named options instance.</summary>
    public string OptionsName { get; } =
        optionsName ?? throw new ArgumentNullException(nameof(optionsName));
}
