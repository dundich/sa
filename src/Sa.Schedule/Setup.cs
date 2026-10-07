using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.Schedule;

/// <summary>
/// Provides extension methods for setting up the scheduling system.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Adds the scheduling system to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add the scheduling system to.</param>
    /// <param name="configure">
    /// An action to configure the scheduling system. Invoked with a fresh
    /// <see cref="IScheduleBuilder"/> per call, so jobs from every call end up in the same
    /// schedule. Serializable settings go through <see cref="IScheduleBuilder.FromConfiguration"/>
    /// (the configuration section) and <see cref="IScheduleBuilder.Options"/> (the pipeline
    /// escape hatch) on the same builder — a call that uses either counts as a *configuring*
    /// call, while job-only calls stay repeatable.
    /// </param>
    /// <returns>The <see cref="IServiceCollection"/> with the scheduling system added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a second *configuring* call — one whose builder calls
    /// <see cref="IScheduleBuilder.FromConfiguration"/> or <see cref="IScheduleBuilder.Options"/> —
    /// is made in this collection.
    /// </exception>
    /// <remarks>
    /// Safe to call more than once: the core services are registered with <c>TryAdd</c> and jobs
    /// accumulate, which is how libraries that contribute jobs compose
    /// (<c>AddSaPartitional</c> adds migration + cleanup, one <c>AddDeliveryJob</c> per consumer
    /// group). Global settings added through <see cref="IScheduleBuilder.AddErrorHandler"/> and
    /// <see cref="IScheduleBuilder.UseHostedService"/> accumulate as well — they are stored in
    /// the container, not on the builder.
    /// <para>
    /// Every call must complete before the first <see cref="IScheduler"/> is resolved: the
    /// scheduler is a singleton and snapshots <see cref="IScheduleSettings"/> when constructed,
    /// so jobs registered afterwards are silently ignored.
    /// </para>
    /// <para>
    /// <b>Configuration precedence.</b> The options pipeline runs in a fixed order:
    /// <c>BindConfiguration</c> (raw values) → <c>Configure</c>/<c>PostConfigure</c> calls made
    /// through <see cref="IScheduleBuilder.Options"/> → validation, so configuration wins over code — for
    /// every job, <see cref="JobOptions.Disabled"/>, <see cref="JobOptions.Immediate"/> and
    /// <see cref="JobOptions.IsRunOnce"/> (all three in both directions — e.g. <c>Disabled: false</c>
    /// re-enables a job disabled in code, <c>Immediate: false</c> reverts a code
    /// <c>StartImmediate()</c>), <see cref="JobOptions.Cron"/> / <see cref="JobOptions.Every"/>
    /// (replace any timing set in code), <see cref="JobOptions.InitialDelay"/>,
    /// <see cref="JobOptions.ConcurrencyLimit"/> and <see cref="JobOptions.MaxConcurrency"/> are
    /// applied on top of the code settings. A job is
    /// matched to a <c>Jobs:&lt;key&gt;</c> section by its
    /// <see cref="IJobProperties.JobName"/> (<c>WithName(...)</c>), falling back to
    /// <c>typeof(Job).FullName</c> when no name was set — renaming a job in code silently detaches
    /// it from its configuration section. Sections whose key matches no registered job are
    /// ignored. <see cref="ValidateOnStart()"/> turns an invalid configuration into an
    /// <see cref="OptionsValidationException"/> at host start, and the same check also fires when
    /// <c>IOptions&lt;ScheduleOptions&gt;.Value</c> is first read (inside the construction of the
    /// schedule settings), so a bare <c>ServiceCollection</c> fails fast on the first scheduler
    /// resolve rather than deep in a job run.
    /// </para>
    /// <para>
    /// <b>Repeated calls.</b> This method is legitimately called more than once per container by
    /// libraries that contribute jobs (<c>AddSaPartitional</c> and
    /// <c>AddSaOutboxUsingPostgreSql</c> each do so by design). Those internal calls carry no
    /// options configuration — no <see cref="IScheduleBuilder.FromConfiguration"/>, no
    /// <see cref="IScheduleBuilder.Options"/> — so they deduplicate on the options pipeline and are
    /// deliberately not rejected. The guard fires only on a second call that *carries options
    /// configuration*, because that is the one shape that would stack two
    /// <c>Configure</c> callbacks onto the same <see cref="ScheduleOptions"/> instance and
    /// silently merge them.
    /// </para>
    /// <para>
    /// <see cref="TimeProvider"/> is registered as <see cref="TimeProvider.System"/> when absent,
    /// so a test can register its own before building the provider.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaSchedule(
        this IServiceCollection services,
        Action<IScheduleBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var markerExists = services.Any(d => d.ServiceType == typeof(ScheduleOptionsConfigurationMarker));

        // Registered before `configure` so a caller can Replace any of them from inside the
        // callback — TryAdd would not undo a later Add, and Replace needs a prior registration.
        AddSaScheduleCore(services);

        var optionsBuilder = services.AddOptions<ScheduleOptions>();

        ScheduleBuilder? builder = null;

        if (configure is not null)
        {
            builder = new ScheduleBuilder(services);
            configure(builder);
        }

        // Both configuring intents — FromConfiguration(...) and Options(...) — live inside
        // `configure`, so they are only knowable once it has run: the guard fires here, before
        // either is applied to the pipeline. By this point the call has already registered
        // its jobs; the exception still aborts the registration, so no provider is ever built
        // from the half-configured collection.
        var sectionPath = builder?.ConfigSectionPath;

        if (markerExists && (sectionPath is not null || builder is { SettingsActions.Count: > 0 }))
        {
            throw SecondConfiguringCall();
        }

        // The options pipeline, in the house order: raw values (section binding) → the caller's
        // Configure/PostConfigure → validation.
        if (sectionPath is not null)
        {
            optionsBuilder.BindConfiguration(sectionPath);
        }

        optionsBuilder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT. Registered by type via
        // TryAddEnumerable, so a repeated bare call deduplicates instead of stacking.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ScheduleOptions>, ScheduleOptionsValidator>());

        // Replayed in this slot — after the section binding — so an Options(...).Configure
        // has the last word over configuration, and its Validate runs after this method's
        // own checks.
        if (builder is { SettingsActions.Count: > 0 })
        {
            foreach (var settingsAction in builder.SettingsActions)
            {
                settingsAction(optionsBuilder);
            }
        }

        if (sectionPath is not null || builder is { SettingsActions.Count: > 0 })
        {
            services.TryAddSingleton(new ScheduleOptionsConfigurationMarker());
        }

        return services;
    }

    private static InvalidOperationException SecondConfiguringCall() => new(
        "AddSaSchedule options have already been configured in this service collection. " +
        "A second configuring call would stack another Configure callback onto the same " +
        "ScheduleOptions instance, so the settings would silently merge. " +
        "Configure the schedule options only once per service collection.");

    private static void AddSaScheduleCore(IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IScheduler, Scheduler>();
        services.TryAddSingleton<IJobFactory, JobFactory>();
        services.TryAddSingleton<IJobRunner, JobRunner>();
        services.TryAddSingleton<IJobErrorHandler, JobErrorHandler>();

        // Not a side effect of `new ScheduleBuilder(...)` — the settings must exist even when
        // no builder is created, or a call to AddSaSchedule() without `configure` would leave
        // the scheduler unresolvable.
        ScheduleSettingsRegistration.AddTo(services);
    }
}

/// <summary>
/// Sentinel marker recording that <see cref="Setup.AddSaSchedule"/> was configured (with a
/// builder whose <c>FromConfiguration(...)</c> or <c>Options(...)</c> was used) in this collection,
/// so a second *configuring* call fails fast instead of silently stacking two
/// <c>Configure</c> callbacks onto the same <see cref="ScheduleOptions"/> instance. Job-only
/// calls — the way downstream libraries contribute jobs — neither check nor create it.
/// </summary>
internal sealed class ScheduleOptionsConfigurationMarker
{
}
