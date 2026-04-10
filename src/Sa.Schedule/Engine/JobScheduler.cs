using Microsoft.Extensions.Primitives;
using Sa.Classes;

namespace Sa.Schedule.Engine;

internal sealed class JobScheduler(
    Guid jobId,
    IJobRunner runner,
    Func<IJobController> createController) : IJobScheduler
{
    private readonly static IChangeToken NoneChangeToken
        = new CancellationChangeToken(CancellationToken.None);

    private readonly Lock _lock = new();
    private CancellationTokenSource _stoppingTokenSource = new();
    private CancellationToken _originalToken;


    private bool _disposed;

    private readonly WorkQueue<IJobController> _jobs = new(
        WorkQueueOptions<IJobController>.Create(
            (controller, ct) => runner.Run(controller, ct))
            .WithQueueCapacity(100)
            .WithConcurrencyLimit(1));


    public Guid JobId => jobId;

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return !_disposed && _jobs.ActiveTasks > 0;
            }
        }
    }

    public IChangeToken GetActiveChangeToken()
    {
        lock (_lock)
        {
            if (_disposed) return NoneChangeToken;
            return new CancellationChangeToken(_stoppingTokenSource.Token);
        }
    }

    /// <summary>
    /// Start all jobs
    /// </summary>
    public async Task<bool> Start(CancellationToken cancellationToken)
    {
        if (IsActive) return false;

        CancellationTokenSource tokenToStope;
        CancellationTokenSource newTokenSource;

        lock (_lock)
        {
            if (IsActive) return false;

            tokenToStope = _stoppingTokenSource;

            _originalToken = cancellationToken;
            _stoppingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            newTokenSource = _stoppingTokenSource;
        }

        await tokenToStope.CancelAsync();
        tokenToStope.Dispose();

        await ExecuteJobAsync(newTokenSource.Token);
        return true;
    }

    private async Task ExecuteJobAsync(CancellationToken ct)
    {
        try
        {
            await _jobs.WaitForIdleAsync(_originalToken);
            await _jobs.Enqueue(createController(), ct);
        }
        catch
        {
            // ignore
        }
    }

    public async Task<bool> Restart()
    {
        await Stop();
        return await Start(_originalToken);
    }

    public async Task Stop()
    {
        CancellationTokenSource? tokenSourceToCancel;

        lock (_lock)
        {
            if (_disposed) return;
            tokenSourceToCancel = _stoppingTokenSource;
        }

        if (tokenSourceToCancel != null)
        {
            await tokenSourceToCancel.CancelAsync();
        }

        await _jobs.WaitForIdleAsync(_originalToken);
    }


    public void Dispose()
    {
        if (_disposed)
            return;

        CancellationTokenSource tokenToDispose;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            tokenToDispose = _stoppingTokenSource;
        }

        try
        {
            tokenToDispose.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        _jobs.Dispose();
        tokenToDispose.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await Stop();
        await _jobs.DisposeAsync();
        _stoppingTokenSource.Dispose();
    }
}
