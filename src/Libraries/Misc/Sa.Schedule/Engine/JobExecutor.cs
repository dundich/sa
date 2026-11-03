using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;

internal sealed class JobExecutor : IJob, IDisposable
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

    public JobExecutor(
        IJobSettings settings,
        IInterceptorSettings interceptorSettings,
        IServiceScopeFactory scopeFactory)
    {
        _scope = scopeFactory.CreateScope();

        try
        {
            IJob originalJob = (IJob)_scope.ServiceProvider.GetRequiredKeyedService(settings.JobType, settings.JobId);

            _job = interceptorSettings.Interceptors.Count > 0
                ? interceptorSettings
                    .Interceptors
                    .Reverse()
                    .Aggregate(originalJob, (job, s)
                        => new JobProxy(
                            job,
                            (IJobInterceptor)_scope.ServiceProvider.GetRequiredKeyedService(s.HandlerType, s.Key),
                            s.Key))
                : originalJob;
        }
        catch
        {
            // The job cannot be constructed (missing dependency, throwing constructor) or an
            // interceptor cannot be resolved. The scope was opened just above, so it must not
            // leak on the failure path — the caller never gets an instance to dispose.
            _scope.Dispose();
            throw;
        }
    }

    public IServiceProvider ServiceProvider => _scope.ServiceProvider;

    public void Dispose() => _scope.Dispose();

    public Task Execute(IJobContext context, CancellationToken cancellationToken)
        => _job.Execute(context, cancellationToken);
}
