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

    public ErrorHandlingAction ThenAction => _thenAction;

    /// <summary>
    /// The configured retry count; <see langword="null"/> when
    /// <see cref="IfErrorRetry"/> was not called (the
    /// <see cref="Default.RetryCount"/> default applies at execution time).
    /// </summary>
    public int? RetryCount { get; private set; }

    public Func<Exception, bool>? SuppressError { get; private set; }

    private ErrorHandlingAction _thenAction = Default.Action;

    // ThenAction defaults to CloseApplication, so the value alone cannot tell "the
    // caller chose CloseApplication" from "the caller said nothing". Merge needs
    // that distinction to keep the first explicit choice.
    private bool _thenActionSet;

    internal JobErrorHandling Merge(IJobErrorHandling handling)
    {
        // Same direction as JobProperties.Merge: the aggregate (the earlier
        // registration) keeps its explicit values, and only the gaps are filled
        // from the later one. An explicit ThenCloseApplication() is a choice like
        // any other, so it must not be overwritten by a later ThenStopAllJobs().
        if (!_thenActionSet)
        {
            bool explicitInSource = handling is JobErrorHandling other
                ? other._thenActionSet
                : handling.ThenAction != Default.Action;

            if (explicitInSource)
            {
                _thenAction = handling.ThenAction;
                _thenActionSet = true;
            }
        }

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
        _thenAction = ErrorHandlingAction.CloseApplication;
        _thenActionSet = true;
        return this;
    }

    public IJobErrorHandlingBuilder ThenAbortJob()
    {
        _thenAction = ErrorHandlingAction.AbortJob;
        _thenActionSet = true;
        return this;
    }

    public IJobErrorHandlingBuilder ThenStopAllJobs()
    {
        _thenAction = ErrorHandlingAction.StopAllJobs;
        _thenActionSet = true;
        return this;
    }

    public IJobErrorHandlingBuilder DoSuppressError(Func<Exception, bool>? suppressError = null)
    {
        SuppressError = suppressError ?? Default.SuppressError;
        return this;
    }
}
