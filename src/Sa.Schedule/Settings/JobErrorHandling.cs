using Sa.Extensions;

namespace Sa.Schedule.Settings;

internal sealed class JobErrorHandling : IJobErrorHandling, IJobErrorHandlingBuilder
{
    internal static class Default
    {
        public const ErrorHandlingAction Action = ErrorHandlingAction.CloseApplication;
        public const int RetryCount = 2;
        public readonly static Func<Exception, bool> SuppressError = ex => !ex.IsCritical();
    }

    public ErrorHandlingAction ThenAction { get; private set; } = Default.Action;

    public int RetryCount { get; private set; }

    public Func<Exception, bool>? SuppressError { get; private set; }

    internal JobErrorHandling Merge(IJobErrorHandling handling)
    {
        if (handling.ThenAction != Default.Action) { ThenAction = handling.ThenAction; }
        if (handling.HasRetryAttempts) { RetryCount = handling.RetryCount; }
        if (handling.HasSuppressError) { SuppressError = handling.SuppressError; }
        return this;
    }


    public IJobErrorHandlingBuilder IfErrorRetry(int? count = null)
    {
        RetryCount = count ?? Default.RetryCount;
        return this;
    }

    public IJobErrorHandlingBuilder ThenCloseApplication()
    {
        ThenAction = ErrorHandlingAction.CloseApplication;
        return this;
    }

    public IJobErrorHandlingBuilder ThenStopJob()
    {
        ThenAction = ErrorHandlingAction.AbortJob;
        return this;
    }

    public IJobErrorHandlingBuilder ThenStopAllJobs()
    {
        ThenAction = ErrorHandlingAction.StopAllJobs;
        return this;
    }

    public IJobErrorHandlingBuilder DoSuppressError(Func<Exception, bool>? suppressError = null)
    {
        SuppressError = suppressError ?? Default.SuppressError;
        return this;
    }
}
