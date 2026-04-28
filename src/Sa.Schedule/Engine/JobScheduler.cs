using Microsoft.Extensions.Primitives;
using Sa.Utils.WorkQueue;

namespace Sa.Schedule.Engine;

internal sealed class JobScheduler : IJobScheduler
{
    private readonly static IChangeToken NoneChangeToken
        = new CancellationChangeToken(CancellationToken.None);

    private readonly Lock _lock = new();

    private CancellationTokenSource _stoppingTokenSource = new();
    private CancellationToken _originalToken;

    private bool? _started = false;

    private bool _disposed;

    private readonly SaWorkQueue<IJobController> _jobs;

    private readonly Func<int, IJobController> _createController;

    private readonly IJobRunner _runner;

    private IReadOnlyList<IJobController> _jobControllers = [];


    public JobScheduler(
        IJobSettings settings,
        IJobRunner runner,
        Func<int, IJobController> createController)
    {

        _runner = runner;
        _createController = createController;

        JobId = settings.JobId;

        var concurrency = Math.Max(0, settings.Properties.ConcurrencyLimit ?? 1);
        var maxConcurrency = Math.Max(1, settings.Properties.MaxConcurrency ?? concurrency);

        concurrency = Math.Clamp(concurrency, 0, maxConcurrency);

        _jobs = new SaWorkQueue<IJobController>(SaWorkQueueOptions<IJobController>.Create(CreateJob)
            .WithQueueCapacity(maxConcurrency)
            .WithMaxConcurrency(maxConcurrency)
            .WithConcurrencyLimit(concurrency)
            .WithSingleWriter(true)
        );
    }

    private Task CreateJob(IJobController controller, CancellationToken ct)
        => _runner.Run(controller, ct);

    public Guid JobId { get; }

    public int ConcurrencyLimit
    {
        get => _jobs.ConcurrencyLimit;
        set
        {
            if (_jobs.ConcurrencyLimit == value) return;

            _jobs.ConcurrencyLimit = value;
            RefreshConcurrency();
        }
    }

    public bool IsStarted
    {
        get
        {
            lock (_lock)
            {
                return !_disposed && _started.GetValueOrDefault();
            }
        }
    }


    public int ActiveTasks => _jobs.QueueTasks;

    public IChangeToken StartChangeToken()
    {
        lock (_lock)
        {
            if (_disposed) return NoneChangeToken;
            return new CancellationChangeToken(_stoppingTokenSource.Token);
        }
    }


    public async Task<bool> Start(CancellationToken cancellationToken)
    {
        CancellationToken stoppingToken;

        lock (_lock)
        {
            if (_disposed || (_started == null || _started == true))
            {
                return false;
            }

            _started = null;

            _stoppingTokenSource.Cancel();
            _stoppingTokenSource.Dispose();

            _originalToken = cancellationToken;
            _stoppingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            stoppingToken = _stoppingTokenSource.Token;
        }

        var maxCapacity = _jobs.MaxConcurrency;

        List<IJobController> controllers = new(capacity: maxCapacity);

        try
        {
            await _jobs.WaitForIdleAsync(_originalToken);

            for (var i = 0; i < maxCapacity; i++)
            {
                IJobController controller = _createController(i);
                controller.Pause();
                controllers.Add(controller);
                await _jobs.Enqueue(controller, stoppingToken);
            }
        }
        finally
        {
            lock (_lock)
            {
                _jobControllers = controllers;
                _started = true;
            }
        }

        RefreshConcurrency();

        return true;
    }

    private void RefreshConcurrency()
    {
        IReadOnlyList<IJobController> controllers;

        lock (_lock)
        {
            controllers = _jobControllers;
        }

        int limit = _jobs.ConcurrencyLimit;

        for (int i = 0; i < controllers.Count; i++)
        {
            if (i < limit)
            {
                controllers[i].Resume();
            }
            else
            {
                controllers[i].Pause();
            }
        }
    }

    public async Task Stop()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed || !_started.GetValueOrDefault()) return;

            _stoppingTokenSource.Cancel();
            _started = false;
        }

        await _jobs.WaitForIdleAsync(_originalToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancellationTokenSource ctsStopping;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ctsStopping = _stoppingTokenSource;
        }

        try
        {
            ctsStopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        _jobs.Dispose();

        ctsStopping.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await _jobs.DisposeAsync();

        _stoppingTokenSource.Dispose();
    }
}
