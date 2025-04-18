using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Sa.Classes;


public interface IWork<in TModel>
{
    Task Execute(TModel model, CancellationToken cancellationToken);
}


public interface IWorkWithHandleError<in TModel> : IWork<TModel>
{
    Task HandelError(Exception exception, TModel model, CancellationToken cancellationToken);
}

public interface IWorkQueue<TModel, TWork> : IAsyncDisposable
    where TModel : notnull
    where TWork : IWork<TModel>
{
    WorkQueue<TModel, TWork> Enqueue([NotNull] TModel model, TWork work, CancellationToken cancellationToken = default);
    Task Stop(TModel model);
}


/// <summary>
/// 
/// <seealso cref="https://github.com/stebet/rabbitmq-dotnet-client/blob/1c72f6e0356135c46096c7ea031e1b115de6fd61/projects/RabbitMQ.Client/client/impl/AsyncConsumerWorkService.cs"/>
/// </summary>
/// <typeparam name="TModel"></typeparam>
/// <typeparam name="TWork"></typeparam>
public sealed class WorkQueue<TModel, TWork> : IWorkQueue<TModel, TWork> where TModel : notnull
    where TWork : IWork<TModel>
{
    private readonly ConcurrentDictionary<TModel, WorkPool> _workPools = new();


    public WorkQueue<TModel, TWork> Enqueue([NotNull] TModel model, TWork work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        _workPools.GetOrAdd(model, WorkQueue<TModel, TWork>.StartNewWorkPool(model, cancellationToken)).Enqueue(work);
        return this;
    }

    private static WorkPool StartNewWorkPool(TModel model, CancellationToken cancellationToken)
    {
        var newWorkPool = new WorkPool(model);
        newWorkPool.Start(cancellationToken);
        return newWorkPool;
    }

    public async Task Stop(TModel model)
    {
        if (_workPools.TryRemove(model, out WorkPool? workPool))
        {
            await workPool.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (TModel model in _workPools.Keys)
        {
            await Stop(model);
        }
    }

    private sealed class WorkPool : IAsyncDisposable
    {
        private readonly Channel<TWork> _channel;
        private readonly TModel _model;
        private Task? _worker;
        private readonly CancellationTokenSource _stoppedTokenSource = new();

        public WorkPool([NotNullWhen(true)] TModel model)
        {
            _model = model;
            _channel = Channel.CreateUnbounded<TWork>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public void Start(CancellationToken cancellationToken)
        {
            _worker = Task.Run(() => Loop(cancellationToken), cancellationToken);
        }

        public void Enqueue(TWork work)
        {
            _channel.Writer.TryWrite(work);
        }

        private async Task Loop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out TWork? work))
                {
                    using var stoppingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_stoppedTokenSource.Token, cancellationToken);
                    CancellationToken token = stoppingTokenSource.Token;
                    try
                    {
                        Task task = work.Execute(_model, token);
                        if (!task.IsCompleted)
                        {
                            await task.ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == token)
                    {
                        // ignore
                    }
                    catch (Exception error)
                    {
                        if (work is IWorkWithHandleError<TModel> handler)
                        {
                            await handler.HandelError(error, _model, CancellationToken.None);
                        }
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.Complete();
            await _stoppedTokenSource.CancelAsync();

            if (_worker != null)
            {
                try
                {
                    await _worker;
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
            }

            _stoppedTokenSource.Dispose();
        }
    }
}

