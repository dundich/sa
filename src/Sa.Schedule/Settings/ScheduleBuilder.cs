using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sa.Schedule.Engine;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Schedule.Settings;

internal sealed class ScheduleBuilder : IScheduleBuilder
{
    private readonly IServiceCollection _services;

    public ScheduleBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;

        ScheduleSettingsRegistration.AddTo(_services);
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
        _services.AddSingleton<JobSettings>(jobSettings);

        return new JobBuilder(jobSettings);
    }

    public IScheduleBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        Action<IServiceProvider, IJobBuilder> configure,
        Guid? jobId = null)
            where T : class, IJob
    {
        Guid id = GetId(jobId);
        _services.TryAddKeyedScoped<T>(id);

        _services.AddSingleton<JobSettings>(sp =>
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
        _services.AddSingleton<JobSettings>(jobSettings);

        return new JobBuilder(jobSettings);
    }

    public IScheduleBuilder AddErrorHandler(Func<IJobContext, Exception, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Registered rather than stored: only the first builder's IScheduleSettings factory
        // survives TryAdd, so a field on this instance would be dropped on any later
        // AddSaSchedule call. Every call to AddErrorHandler contributes a handler instead.
        _services.AddSingleton(new ErrorHandlerRegistration(handler));

        return this;
    }

    private static Guid GetId(Guid? jobId) => jobId.GetValueOrDefault(Guid.NewGuid());

    public IScheduleBuilder UseHostedService()
    {
        // A marker, not a flag on this builder: only the first builder's IScheduleSettings
        // factory survives TryAdd, so an instance field would read false for every caller
        // after the first. TryAddSingleton — the marker only has to be present.
        _services.TryAddSingleton(ScheduleHostedServiceMarker.Instance);

        // TryAddEnumerable inside AddHostedService dedupes on the implementation type, so
        // calling UseHostedService from several builders still yields a single ScheduleHost.
        _services.AddHostedService<ScheduleHost>();

        return this;
    }

    public IScheduleBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        object? key = null)
            where T : class, IJobInterceptor
    {
        _services.AddSingleton<JobInterceptorSettings>(new JobInterceptorSettings(typeof(T), key));
        _services.TryAddKeyedScoped<T>(key);
        return this;
    }

    public IScheduleBuilder Options(Action<OptionsBuilder<ScheduleOptions>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }
}
