using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sa.Fixture;

public interface ISaFixture
{
    CancellationToken CancellationToken { get; }
    IServiceProvider ServiceProvider { get; }
    Action<IServiceCollection, IConfiguration>? SetupServices { get; set; }
}

public abstract class SaFixture : IAsyncLifetime, ISaFixture
{
    private readonly Lazy<ServiceProvider> _serviceProvider;

    protected IServiceCollection Services { get; }
        = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>();

    protected IConfigurationRoot Configuration { get; }
        = new ConfigurationBuilder().AddJsonFile("appsettings.json", true).Build();

    public Action<IServiceCollection, IConfiguration>? SetupServices { get; set; }

    protected SaFixture()
    {
        _serviceProvider = new Lazy<ServiceProvider>(() =>
        {
            SetupServices?.Invoke(Services, Configuration);
            return Services.BuildServiceProvider();
        });
    }

    public IServiceProvider ServiceProvider => _serviceProvider.Value;

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async virtual ValueTask DisposeAsync()
    {
        if (_serviceProvider.IsValueCreated) await _serviceProvider.Value.DisposeAsync();
    }
}


public abstract class SaFixture<TSub, TSettings> : SaFixture
     where TSub : notnull
{
    private readonly Lazy<TSub> sub;

    protected SaFixture(TSettings settings) : base()
    {
        Settings = settings;
        sub = new Lazy<TSub>(() => ServiceProvider.GetRequiredService<TSub>());
    }

    public TSub Sub => sub.Value;

    public TSettings Settings { get; }
}


public abstract class SaFixture<TSub>() : SaFixture
     where TSub : notnull
{
    public TSub Sub => ServiceProvider.GetRequiredService<TSub>();
}
