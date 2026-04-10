using Microsoft.Extensions.Primitives;
using Sa.Classes;

namespace Sa.Schedule.Engine;

internal sealed class JobScheduler(
    IJobRunner runner,
    Func<IJobController> createController) : IJobScheduler
{
    private readonly static IChangeToken NoneChangeToken
        = new CancellationChangeToken(CancellationToken.None);

    private readonly Lock _lock = new();
    private CancellationTokenSource _stoppingToken = new();
    private CancellationToken _originalToken;


    private bool _disposed;

    private readonly WorkQueue<IJobController> _jobs = new(
        WorkQueueOptions<IJobController>.Create(
            (controller, ct) => runner.Run(controller, ct))
            .WithQueueCapacity(40)
            .WithConcurrencyLimit(1));

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
            return new CancellationChangeToken(_stoppingToken.Token);
        }
    }

    /// <summary>
    /// Start all jobs
    /// </summary>
    public async Task<bool> Start(CancellationToken cancellationToken)
    {
        if (IsActive) return false;

        CancellationTokenSource tokenToStope;

        lock (_lock)
        {
            if (IsActive) return false;

            tokenToStope = _stoppingToken;

            _originalToken = cancellationToken;
            _stoppingToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        await tokenToStope.CancelAsync();
        tokenToStope.Dispose();

        await ExecuteJobAsync(_stoppingToken.Token);
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
        CancellationTokenSource? tokenToCancel;

        lock (_lock)
        {
            if (_disposed) return;
            tokenToCancel = _stoppingToken;
        }


        // Отмена вне блокировки для минимизации времени удержания лога
        if (tokenToCancel != null)
        {
            await tokenToCancel.CancelAsync();
        }

        await _jobs.WaitForIdleAsync(CancellationToken.None);
    }


    public void Dispose()
    {
        CancellationTokenSource? tokenToDispose;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            tokenToDispose = _stoppingToken;
            _stoppingToken.Cancel();
        }

        _jobs.Dispose();
        tokenToDispose?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        // Корректное ожидание завершения фоновых задач
        await Stop();
        await _jobs.DisposeAsync();
        _stoppingToken?.Dispose();
    }
}
