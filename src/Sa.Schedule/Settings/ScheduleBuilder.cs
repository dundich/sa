using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Schedule.Engine;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Schedule.Settings;

internal class ScheduleBuilder : IScheduleBuilder
{
    private readonly IServiceCollection _services;

    private bool _isHostedService;

    private Func<IJobContext, Exception, bool>? _handleError;

    public ScheduleBuilder(IServiceCollection services)
    {
        _services = services;

        _services.TryAddSingleton<IScheduleSettings>(sp =>
        {
            IEnumerable<JobSettings> jobSettings = sp.GetServices<JobSettings>();
            ScheduleSettings settings = ScheduleSettings.Create(jobSettings, _isHostedService, _handleError);
            return settings;
        });

        _services.TryAddSingleton<IInterceptorSettings>(sp =>
        {
            IEnumerable<JobInterceptorSettings> jobSettings = sp.GetServices<JobInterceptorSettings>();
            return new InterceptorSettings(jobSettings);
        });
    }


    public IJobBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid? jobId = null)
        where T : class, IJob
    {
        Guid id = GetId(jobId);
        _services.TryAddKeyedScoped<T>(id);

        JobSettings jobSettings = JobSettings.Create<T>(id);
        _services.AddSingleton<JobSettings>(jobSettings);

        return new JobBuilder(jobSettings);
    }

    public IScheduleBuilder AddJob<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Action<IServiceProvider, IJobBuilder> configure, Guid? jobId = null)
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
        _services
            .RemoveAllKeyed<Job>(jobId)
            .AddKeyedScoped(id, (_, __) => new Job(action));

        JobSettings jobSettings = JobSettings.Create<Job>(id);
        _services.AddSingleton<JobSettings>(jobSettings);

        return new JobBuilder(jobSettings);
    }

    public IScheduleBuilder AddErrorHandler(Func<IJobContext, Exception, bool> handler)
    {
        _handleError = handler;
        return this;
    }

    private static Guid GetId(Guid? jobId) => jobId.GetValueOrDefault(Guid.NewGuid());

    public IScheduleBuilder UseHostedService()
    {
        _isHostedService = true;
        _services.AddHostedService<ScheduleHost>();
        return this;
    }

    public IScheduleBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(object? key = null)
        where T : class, IJobInterceptor
    {
        _services.AddSingleton<JobInterceptorSettings>(new JobInterceptorSettings(typeof(T), key));
        _services.TryAddKeyedScoped<T>(key);
        return this;
    }
}
