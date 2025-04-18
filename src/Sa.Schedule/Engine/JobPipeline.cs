using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;

internal sealed class JobPipeline : IJob, IDisposable
{
    #region proxy
    class JobProxy(IJob job, IJobInterceptor interceptor, object? key) : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => interceptor.OnHandle(
                context
                , () => job.Execute(context, cancellationToken)
                , key
                , cancellationToken);
    }
    #endregion

    private readonly IServiceScope _scope;
    private readonly IJob _job;

    public JobPipeline(IJobSettings settings, IInterceptorSettings interceptorSettings, IServiceScopeFactory scopeFactory)
    {
        _scope = scopeFactory.CreateScope();
        IJob originalJob = (IJob)_scope.ServiceProvider.GetRequiredKeyedService(settings.JobType, settings.JobId);

        if (interceptorSettings.Interceptors.Count > 0)
        {
            _job = interceptorSettings
                .Interceptors
                .Reverse()
                .Aggregate(originalJob, (job, s)
                    => new JobProxy(job, (IJobInterceptor)_scope.ServiceProvider.GetRequiredKeyedService(s.HandlerType, s.Key), s.Key));
        }
        else
        {
            _job = originalJob;
        }
    }

    public IServiceProvider JobServices => _scope.ServiceProvider;

    public void Dispose() => _scope.Dispose();

    public Task Execute(IJobContext context, CancellationToken cancellationToken) => _job.Execute(context, cancellationToken);
}
