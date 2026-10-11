using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sa.Schedule.Engine;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Schedule.Settings;

internal sealed class ScheduleBuilder : IScheduleBuilder
{
    private readonly IServiceCollection _services;

    /// <summary>
    /// The keyed DI key of this schedule, or <c>null</c> for the default (unkeyed) schedule.
    /// Everything the builder registers lands under this key — job settings, interceptors,
    /// error handlers and the hosted-service marker — so schedules never see each other's jobs.
    /// </summary>
    private readonly string? _scheduleKey;

    public ScheduleBuilder(IServiceCollection services, string? scheduleKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _scheduleKey = scheduleKey;

        ScheduleSettingsRegistration.AddTo(_services, scheduleKey);
    }

    // ---------- settings: the standard options pipeline ----------

    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after section binding.</summary>
    private readonly List<Action<OptionsBuilder<ScheduleOptions>>> _settingsActions = [];

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<ScheduleOptions>>> SettingsActions => _settingsActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup before the <see cref="Options"/> actions.</summary>
    public string? ConfigSectionPath { get; private set; }

    public IScheduleBuilder FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }


    public IJobBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        Guid? jobId = null)
            where T : class, IJob
    {
        Guid id = GetId(jobId);
        _services.TryAddKeyedScoped<T>(id);

        JobSettings jobSettings = JobSettings.Create<T>(id);
        AddJobSettings(jobSettings);

        return new JobBuilder(jobSettings);
    }

    public IScheduleBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        Action<IServiceProvider, IJobBuilder> configure,
        Guid? jobId = null)
            where T : class, IJob
    {
        Guid id = GetId(jobId);
        _services.TryAddKeyedScoped<T>(id);

        AddJobSettings(sp =>
        {
            JobSettings jobSettings = JobSettings.Create<T>(id);
            configure.Invoke(sp, new JobBuilder(jobSettings));
            return jobSettings;
        });

        return this;
    }

    public IJobBuilder AddJob(Func<IJobContext, CancellationToken, Task> action, Guid? jobId = null)
    {
        Guid id = GetId(jobId);

        // Remove by the actual key being registered (id), not by the raw
        // parameter: with a null jobId the parameter can never match the
        // generated key, and the removal would be a silent no-op instead of
        // replacing a previous registration of the same job.
        _services
            .RemoveAllKeyed<FuncJob>(id)
            .AddKeyedScoped(id, (_, __) => new FuncJob(action));

        JobSettings jobSettings = JobSettings.Create<FuncJob>(id);
        AddJobSettings(jobSettings);

        return new JobBuilder(jobSettings);
    }

    public IScheduleBuilder AddErrorHandler(Func<IJobContext, Exception, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Registered rather than stored: the settings are built from the container, so a field
        // on this instance would be dropped on any later AddSaSchedule call. Every call to
        // AddErrorHandler contributes a handler — keyed per schedule, so named schedules get
        // their own handlers and the default one keeps its own.
        if (_scheduleKey is null)
        {
            _services.AddSingleton(new ErrorHandlerRegistration(handler));
        }
        else
        {
            _services.AddKeyedSingleton<ErrorHandlerRegistration>(
                _scheduleKey, new ErrorHandlerRegistration(handler));
        }

        return this;
    }

    private static Guid GetId(Guid? jobId) => jobId.GetValueOrDefault(Guid.NewGuid());

    public IScheduleBuilder UseHostedService()
    {
        if (_scheduleKey is null)
        {
            // A marker, not a flag on this builder: only the first builder's IScheduleSettings
            // factory survives TryAdd, so an instance field would read false for every caller
            // after the first. TryAddSingleton — the marker only has to be present.
            _services.TryAddSingleton(ScheduleHostedServiceMarker.Instance);

            // TryAddEnumerable inside AddHostedService dedupes on the implementation type, so
            // calling UseHostedService from several builders still yields a single ScheduleHost.
            _services.AddHostedService<ScheduleHost>();
        }
        else
        {
            // .NET has no keyed hosted services, so each named schedule gets its own IHostedService
            // registration whose factory resolves its keyed scheduler. The keyed marker guards the
            // idempotency: the hosted service is added only the first time this schedule name asks
            // for it (the marker itself is TryAdd).
            bool already = _services.Any(d =>
                d.ServiceType == typeof(ScheduleHostedServiceMarker)
                && Equals(d.ServiceKey, _scheduleKey));

            _services.TryAddKeyedSingleton<ScheduleHostedServiceMarker>(
                _scheduleKey, ScheduleHostedServiceMarker.Instance);

            if (!already)
            {
                _services.AddSingleton<IHostedService>(sp =>
                    new ScheduleHost(sp.GetRequiredKeyedService<IScheduler>(_scheduleKey)));
            }
        }

        return this;
    }

    public IScheduleBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        object? key = null)
            where T : class, IJobInterceptor
    {
        var settings = new JobInterceptorSettings(typeof(T), key);

        if (_scheduleKey is null)
        {
            _services.AddSingleton<JobInterceptorSettings>(settings);
        }
        else
        {
            // The interceptor collection is keyed by the schedule, the interceptor itself by its
            // own key (a schedule can reuse the same key across types without collision).
            _services.AddKeyedSingleton<JobInterceptorSettings>(_scheduleKey, settings);
        }

        _services.TryAddKeyedScoped<T>(key);
        return this;
    }

    public IScheduleBuilder Options(Action<OptionsBuilder<ScheduleOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }

    private void AddJobSettings(JobSettings jobSettings)
    {
        jobSettings.ScheduleKey ??= _scheduleKey;

        if (_scheduleKey is null)
        {
            _services.AddSingleton(jobSettings);
        }
        else
        {
            _services.AddKeyedSingleton<JobSettings>(_scheduleKey, jobSettings);
        }
    }

    private void AddJobSettings(Func<IServiceProvider, JobSettings> factory)
    {
        if (_scheduleKey is null)
        {
            _services.AddSingleton(factory);
        }
        else
        {
            _services.AddKeyedSingleton<JobSettings>(_scheduleKey, (sp, _) => factory(sp));
        }
    }
}
