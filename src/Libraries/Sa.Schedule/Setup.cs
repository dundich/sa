using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    /// The key of the default schedule — the one registered by the keyless
    /// <see cref="AddSaSchedule(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{IScheduleBuilder})"/>
    /// overload (and by internal libraries such as <c>AddSaPartitional</c> / <c>AddDeliveryJob</c>).
    /// The default schedule is resolved unkeyed (<c>GetRequiredService&lt;IScheduler&gt;()</c>); as a
    /// convenience it is also registered as a keyed service under this key, so
    /// <c>GetRequiredKeyedService&lt;IScheduler&gt;(Setup.DefaultName)</c> works too.
    /// <c>AddSaSchedule(DefaultName, ...)</c> registers the default schedule.
    /// </summary>
    public const string DefaultName = "default";

    /// <summary>
    /// Adds the default scheduling system to the service collection.
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
    /// is made for the default schedule.
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
    /// The default schedule is resolved unkeyed. A keyed lookup under
    /// <see cref="DefaultName"/> is also registered (forwarding to the same instance), so
    /// consumer code can use a single resolution style across schedules.
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
    /// libraries that contribute jobs (<c>AddSaPartitional</c> for the default module, and
    /// <c>AddSaOutboxUsingPostgreSql</c> / the Outbox delivery builder for the outbox's own
    /// <b>named</b> schedule — see <c>OutboxScope.DefaultKey</c> — each do so by design). Those
    /// internal calls carry no
    /// options configuration — no <see cref="IScheduleBuilder.FromConfiguration"/>, no
    /// <see cref="IScheduleBuilder.Options"/> — so they deduplicate on the options pipeline and are
    /// deliberately not rejected. The guard fires only on a second call that *carries options
    /// configuration*, because that is the one shape that would stack two
    /// <c>Configure</c> callbacks onto the same <see cref="ScheduleOptions"/> instance and
    /// silently merge them. It fires per schedule: a configuring call for one named schedule does
    /// not block a configuring call for another.
    /// </para>
    /// <para>
    /// <see cref="TimeProvider"/> is registered as <see cref="TimeProvider.System"/> when absent,
    /// so a test can register its own before building the provider.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaSchedule(
        this IServiceCollection services,
        Action<IScheduleBuilder>? configure = null)
        => AddSaScheduleWithKey(services, scheduleKey: null, configure);

    /// <summary>
    /// Adds an independently configured scheduling system under the given name.
    /// </summary>
    /// <param name="services">The service collection to add the scheduling system to.</param>
    /// <param name="scheduleName">
    /// The unique name of the schedule. Every part of the registration is keyed by it: the
    /// <see cref="IScheduler"/> / <see cref="IScheduleSettings"/> / <see cref="IJobFactory"/> /
    /// <see cref="IJobErrorHandler"/> singletons, the job settings, the interceptors and the
    /// error handlers. Resolve the scheduler with
    /// <c>sp.GetRequiredKeyedService&lt;IScheduler&gt;(scheduleName)</c>. A call with
    /// <see cref="DefaultName"/> registers the default (unkeyed) schedule.
    /// </param>
    /// <param name="configure">
    /// An action to configure the scheduling system. Same shape as the keyless overload — jobs,
    /// interceptors, error handlers, <see cref="IScheduleBuilder.FromConfiguration"/> and
    /// <see cref="IScheduleBuilder.Options"/> — but everything lands in this schedule's own
    /// keyed slot, including its own named <see cref="ScheduleOptions"/> pipeline.
    /// </param>
    /// <returns>The <see cref="IServiceCollection"/> with the scheduling system added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scheduleName"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a second *configuring* call — one whose builder calls
    /// <see cref="IScheduleBuilder.FromConfiguration"/> or <see cref="IScheduleBuilder.Options"/> —
    /// is made for the same schedule name.
    /// </exception>
    /// <remarks>
    /// Multiple schedules fully coexist: each one carries its own jobs, interceptors, global
    /// error handlers and <see cref="ScheduleOptions"/> (bound from its own
    /// <c>FromConfiguration</c> section to its own named options instance). Repeated calls with
    /// the same name accumulate jobs into that schedule, exactly like the keyless overload does
    /// for the default schedule. An explicit <c>jobId</c> must be unique within its schedule
    /// across job types (the same type with the same id merges into one job); auto-generated
    /// ids never clash.
    /// </remarks>
    public static IServiceCollection AddSaSchedule(
        this IServiceCollection services,
        string scheduleName,
        Action<IScheduleBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);

        // "default" is not a separate keyed schedule — it IS the default schedule, so the
        // keyless and keyed APIs cannot double-register the same under it.
        return AddSaScheduleWithKey(services, scheduleName == DefaultName ? null : scheduleName, configure);
    }

    private static IServiceCollection AddSaScheduleWithKey(
        IServiceCollection services,
        string? scheduleKey,
        Action<IScheduleBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Snapshot for rollback: a rejected configuring call must not leave half of its
        // registrations (core services and jobs) behind in the collection.
        int serviceCount = services.Count;

        var markerExists = scheduleKey is null
            ? services.Any(d => d.ServiceType == typeof(ScheduleOptionsConfigurationMarker))
            : services.Any(d =>
                d.ServiceType == typeof(ScheduleOptionsConfigurationMarker)
                && Equals(d.ServiceKey, scheduleKey));

        // Registered before `configure` so a caller can Replace any of them from inside the
        // callback — TryAdd would not undo a later Add, and Replace needs a prior registration.
        AddSaScheduleCore(services, scheduleKey);

        var optionsBuilder = scheduleKey is null
            ? services.AddOptions<ScheduleOptions>()
            : services.AddOptions<ScheduleOptions>(scheduleKey);

        ScheduleBuilder? builder = null;

        if (configure is not null)
        {
            builder = new ScheduleBuilder(services, scheduleKey);
            configure(builder);
        }

        // Both configuring intents — FromConfiguration(...) and Options(...) — live inside
        // `configure`, so they are only knowable once it has run: the guard fires here, before
        // either is applied to the pipeline. The registrations this call already appended are
        // rolled back, so the rejected call leaves the collection exactly as it found it.
        var sectionPath = builder?.ConfigSectionPath;

        if (markerExists && (sectionPath is not null || builder is { SettingsActions.Count: > 0 }))
        {
            // The call is rejected as a whole, so roll back what it appended: core
            // registrations, jobs and any services the configure callback registered.
            // Leaving them behind would make the container differ from what the caller
            // sees, and a later job-only call would silently inherit them.
            while (services.Count > serviceCount)
            {
                services.RemoveAt(services.Count - 1);
            }

            throw SecondConfiguringCall(scheduleKey);
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
        // TryAddEnumerable — repeated calls deduplicate instead of stacking. The single
        // validator runs for every named options instance, so no per-name registration needed.
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
            if (scheduleKey is null)
            {
                services.TryAddSingleton(new ScheduleOptionsConfigurationMarker());
            }
            else
            {
                services.TryAddKeyedSingleton(scheduleKey, new ScheduleOptionsConfigurationMarker());
            }
        }

        return services;
    }

    private static InvalidOperationException SecondConfiguringCall(string? scheduleKey) => new(
        scheduleKey is null
            ? "AddSaSchedule options have already been configured in this service collection. " +
              "A second configuring call would stack another Configure callback onto the same " +
              "ScheduleOptions instance, so the settings would silently merge. " +
              "Configure the schedule options only once per service collection."
            : $"AddSaSchedule options for schedule '{scheduleKey}' have already been configured. " +
              "A second configuring call would stack another Configure callback onto the same " +
              "ScheduleOptions instance, so the settings would silently merge. " +
              "Configure the schedule options only once per schedule name.");

    private static void AddSaScheduleCore(IServiceCollection services, string? scheduleKey)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IJobRunner, JobRunner>();

        if (scheduleKey is null)
        {
            services.TryAddSingleton<IScheduler, Scheduler>();
            services.TryAddSingleton<IJobFactory, JobFactory>();

            // Registered through a factory, not by implementation type: both
            // IHostApplicationLifetime and ILogger<JobErrorHandler> are optional,
            // and a nullable annotation does not make a constructor parameter
            // optional for DI. Implementation-type registration therefore made the
            // handler unresolvable in any container without a Generic Host (a bare
            // ServiceCollection, a test fixture) — and JobController's error path
            // then silently aborted the job instead of applying the policy. The
            // keyed registration below has always used this shape.
            services.TryAddSingleton<IJobErrorHandler>(static sp => new JobErrorHandler(
                sp.GetRequiredService<IScheduleSettings>(),
                sp.GetService<IHostApplicationLifetime>(),
                sp.GetService<ILogger<JobErrorHandler>>()));

            // Not a side effect of `new ScheduleBuilder(...)` — the settings must exist even when
            // no builder is created, or a call to AddSaSchedule() without `configure` would leave
            // the scheduler unresolvable.
            ScheduleSettingsRegistration.AddTo(services);

            // Keyed aliases for the default schedule, so GetRequiredKeyedService<...>(
            // Setup.DefaultName) resolves it — one resolution style across schedules.
            services.TryAddKeyedSingleton<IScheduler>(
                DefaultName, static (sp, _) => sp.GetRequiredService<IScheduler>());
            services.TryAddKeyedSingleton<IScheduleSettings>(
                DefaultName, static (sp, _) => sp.GetRequiredService<IScheduleSettings>());
        }
        else
        {
            services.TryAddKeyedSingleton<IScheduler>(
                scheduleKey, static (sp, key) => new Scheduler(
                    sp.GetRequiredKeyedService<IScheduleSettings>(key),
                    sp.GetRequiredKeyedService<IJobFactory>(key),
                    sp.GetService<ILogger<Scheduler>>()));

            services.TryAddKeyedSingleton<IJobFactory>(
                scheduleKey, static (sp, key) => new JobFactory(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredKeyedService<IInterceptorSettings>(key),
                    sp.GetRequiredService<IJobRunner>(),
                    sp.GetService<TimeProvider>(),
                    sp.GetService<ILogger<JobScheduler>>()));

            services.TryAddKeyedSingleton<IJobErrorHandler>(
                scheduleKey, static (sp, key) => new JobErrorHandler(
                    sp.GetRequiredKeyedService<IScheduleSettings>(key),
                    sp.GetService<IHostApplicationLifetime>(),
                    sp.GetService<ILogger<JobErrorHandler>>(),
                    key as string));

            ScheduleSettingsRegistration.AddTo(services, scheduleKey);
        }
    }
}

/// <summary>
/// Sentinel marker recording that <see cref="Setup.AddSaSchedule"/> was configured (with a
/// builder whose <c>FromConfiguration(...)</c> or <c>Options(...)</c> was used) for a schedule,
/// so a second *configuring* call for that schedule fails fast instead of silently stacking two
/// <c>Configure</c> callbacks onto the same <see cref="ScheduleOptions"/> instance. Job-only
/// calls — the way downstream libraries contribute jobs — neither check nor create it. The
/// keyed (named) schedules carry it under their key, the default schedule unkeyed.
/// </summary>
internal sealed class ScheduleOptionsConfigurationMarker
{
}