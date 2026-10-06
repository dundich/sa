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

    /// <summary>
    /// The configured retry count; <see langword="null"/> when
    /// <see cref="IfErrorRetry"/> was not called (the
    /// <see cref="Default.RetryCount"/> default applies at execution time).
    /// </summary>
    public int? RetryCount { get; private set; }

    public Func<Exception, bool>? SuppressError { get; private set; }

    internal JobErrorHandling Merge(IJobErrorHandling handling)
    {
        // Same direction as JobProperties.Merge: the aggregate (the earlier
        // registration) keeps its explicit values, and only the gaps are filled
        // from the later one. (Previously this merged the other way — the later
        // registration's explicit values overwrote the aggregate's — which
        // disagreed with every other merged property, where the first
        // registration wins.)
        if (ThenAction == Default.Action) { ThenAction = handling.ThenAction; }
        if (RetryCount is null) { RetryCount = handling.RetryCount; }
        if (SuppressError is null) { SuppressError = handling.SuppressError; }
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

    public IJobErrorHandlingBuilder ThenAbortJob()
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
