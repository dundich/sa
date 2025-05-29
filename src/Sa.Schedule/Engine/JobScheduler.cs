using Microsoft.Extensions.Primitives;

namespace Sa.Schedule.Engine;

internal sealed class JobScheduler(IJobRunner runner, IJobController controller) : IJobScheduler, IDisposable, IAsyncDisposable
{
    private readonly static IChangeToken NoneChangeToken = new CancellationChangeToken(CancellationToken.None);

    private readonly Lock _locked = new();

    private TaskCompletionSource? _stoppingTask;

    private CancellationTokenSource _stoppingToken = new();
    private CancellationToken _originalToken;

    private bool _disposed;

    public IJobContext Context => controller.Context;

    public bool IsActive => _stoppingTask?.Task.Status == TaskStatus.WaitingForActivation;

    public IChangeToken GetActiveChangeToken()
    {
        if (_disposed) return NoneChangeToken;

        lock (_locked)
        {
            return new CancellationChangeToken(_stoppingToken.Token);
        }
    }

    /// <summary>
    /// Start all jobs
    /// </summary>
    public bool Start(CancellationToken cancellationToken)
    {
        if (IsActive) return false;

        lock (_locked)
        {
            _stoppingToken.Cancel();
            _stoppingToken.Dispose();

            _originalToken = cancellationToken;
            _stoppingToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _stoppingTask = new TaskCompletionSource();

            Task task = runner.Run(controller, _stoppingToken.Token);

            task.ContinueWith(Done, CancellationToken.None);

            return true;
        }
    }

    public bool Restart() => Start(_originalToken);

    public Task Stop()
    {
        if (IsActive)
        {
            lock (_locked)
            {
                _stoppingToken?.Cancel();
            }
        }

        return _stoppingTask?.Task ?? Task.CompletedTask;
    }

    private void Done(Task task)
    {
        lock (_locked)
        {
            _stoppingTask?.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ = Stop();
            _stoppingToken.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await Stop();
            _stoppingToken.Dispose();
        }
    }
}
