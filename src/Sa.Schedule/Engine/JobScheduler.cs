using Microsoft.Extensions.Primitives;
using Sa.Classes;

namespace Sa.Schedule.Engine;

internal sealed class JobScheduler(
    IJobSettings settings,
    IJobRunner runner,
    Func<IJobController> createController) : IJobScheduler
{
    private readonly static IChangeToken NoneChangeToken
        = new CancellationChangeToken(CancellationToken.None);

    private readonly Lock _lock = new();
    private CancellationTokenSource _stoppingTokenSource = new();
    private CancellationToken _originalToken;

    private bool _started = false;


    private bool _disposed;

    private readonly WorkQueue<IJobController> _jobs = new(
        WorkQueueOptions<IJobController>.Create((controller, ct) => runner.Run(controller, ct))
            .WithQueueCapacity(settings.Properties.MaxConcurrencyLimit
                ?? Environment.ProcessorCount)
            .WithMaxConcurrencyLimit(settings.Properties.MaxConcurrencyLimit
                ?? Environment.ProcessorCount)
            .WithConcurrencyLimit(settings.Properties.ConcurrencyLimit
                ?? 1)
            .WithSingleWriter(settings.Properties.SingleWriter
                ?? false)
        );


    public Guid JobId => settings.JobId;


    public int ConcurrencyLimit
    {
        get => _jobs.ConcurrencyLimit;
        set
        {
            if (_jobs.ConcurrencyLimit != value)
            {
                _jobs.ConcurrencyLimit = Math.Min(value, _jobs.MaxConcurrency);
                _ = Restart();
            }
        }
    }


    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return !_disposed && _started;
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

        return await StartAsync(cancellationToken);
    }

    private ValueTask<bool> StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {

            if (!_disposed && _started) return ValueTask.FromResult(false);


            _started = true;

            _stoppingTokenSource.Cancel();
            _stoppingTokenSource.Dispose();

            _originalToken = cancellationToken;
            _stoppingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            return StartConcurrencyAsync(_stoppingTokenSource.Token);
        }
    }

    private async ValueTask<bool> StartConcurrencyAsync(CancellationToken ct)
    {
        try
        {
            var limit = _jobs.ConcurrencyLimit;

            await _jobs.WaitForIdleAsync(ct);

            for (int i = 0; i < limit; i++)
            {
                await _jobs.Enqueue(createController(), ct);
            }
        }
        catch
        {
            // ignore
        }
        return IsActive;
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

        lock (_lock)
        {
            _started = false;
        }
    }


    public void Dispose()
    {

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
        lock (_lock)
        {
            if (_disposed) return;
        }

        await Stop();

        lock (_lock)
        {
            _disposed = true;
        }

        await _jobs.DisposeAsync();
        _stoppingTokenSource.Dispose();
    }
}
