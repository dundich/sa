using Microsoft.Extensions.DependencyInjection;

namespace Sa.ScheduleTests;

/// <summary>
/// Tiny in-memory DI container used to exercise the real <c>JobExecutor</c> /
/// <c>JobController</c> without a mocking framework.
///
/// Services are registered up-front on an <see cref="IServiceCollection"/> and
/// resolved by the genuine <see cref="ServiceProvider"/> (so keyed-service
/// lookups behave exactly as in production), while a thin
/// <see cref="TrackingScope"/> wrapper records disposal — the resource that
/// would leak on a bug.
/// </summary>
internal sealed class TrackingScopeFactory : IServiceScopeFactory
{
    private readonly IServiceCollection _services;
    private readonly ServiceProvider _provider;

    private TrackingScopeFactory(IServiceCollection services)
    {
        _services = services;
        _provider = services.AddOptions().BuildServiceProvider();
    }

    public static TrackingScopeFactory Create() => new(new ServiceCollection());

    public static TrackingScopeFactory Create(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return new(services);
    }

    private readonly List<TrackingScope> _scopes = [];

    public IReadOnlyList<TrackingScope> Scopes => _scopes;

    public IServiceScope CreateScope()
    {
        var inner = _provider.CreateScope();
        var scope = new TrackingScope(inner);
        _scopes.Add(scope);
        return scope;
    }
}

/// <summary>
/// A scope that wraps a real <see cref="IServiceScope"/> and records disposal.
/// All resolution is forwarded to the wrapped provider.
/// </summary>
internal sealed class TrackingScope : IServiceScope, IServiceProvider, IAsyncDisposable
{
    private readonly IServiceScope _inner;
    private bool _disposed;

    public TrackingScope(IServiceScope inner)
    {
        _inner = inner;
    }

    public IServiceProvider ServiceProvider => _inner.ServiceProvider;

    public object? GetService(Type serviceType) => _inner.ServiceProvider.GetService(serviceType);

    public object? GetRequiredService(Type serviceType) =>
        _inner.ServiceProvider.GetRequiredService(serviceType);

    public object? GetRequiredKeyedService(Type serviceType, object? key) =>
        _inner.ServiceProvider.GetRequiredKeyedService(serviceType, key);

    public object? GetKeyedService(Type serviceType, object? key) =>
        _inner.ServiceProvider.GetKeyedService(serviceType, key);

    public bool Disposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inner.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_inner is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _inner.Dispose();
        }
    }
}
