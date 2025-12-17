namespace Sa.Classes;


internal sealed class AsyncManualResetEvent
{
    private readonly Lock _syncRoot = new ();
    private TaskCompletionSource _tcs = new (TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isSignaled;

    public AsyncManualResetEvent(bool initialSet = false)
    {
        _isSignaled = initialSet;
        if (initialSet)
        {
            _tcs.TrySetResult();
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (_isSignaled)
            {
                return Task.CompletedTask;
            }

            return _tcs.Task.WaitAsync(cancellationToken);
        }
    }

    public void Set()
    {
        lock (_syncRoot)
        {
            if (!_isSignaled)
            {
                _isSignaled = true;
                _tcs.TrySetResult();
            }
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            if (_isSignaled)
            {
                _isSignaled = false;
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public bool IsSet
    {
        get
        {
            lock (_syncRoot)
            {
                return _isSignaled;
            }
        }
    }
}