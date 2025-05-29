using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sa.Fixture;


public abstract class SaFixture<TSettings> : IAsyncLifetime
{
    private readonly Lazy<ServiceProvider> _serviceProvider;

    protected IServiceCollection Services { get; }
        = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>();

    protected IConfigurationRoot Configuration { get; }
        = new ConfigurationBuilder().AddJsonFile("appsettings.json", true).Build();

    public Action<IServiceCollection, IConfiguration>? SetupServices { get; set; }

    protected SaFixture(TSettings settings)
    {
        Settings = settings;
        _serviceProvider = new Lazy<ServiceProvider>(() =>
        {
            SetupServices?.Invoke(Services, Configuration);
            return Services.BuildServiceProvider();
        });
    }

    public TSettings Settings { get; }

    public IServiceProvider ServiceProvider => _serviceProvider.Value;

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async virtual ValueTask DisposeAsync()
    {
        if (_serviceProvider.IsValueCreated) await _serviceProvider.Value.DisposeAsync();
    }
}


public abstract class SaFixture<TSub, TSettings> : SaFixture<TSettings>
     where TSub : notnull
{
    private readonly Lazy<TSub> sub;

    protected SaFixture(TSettings settings) : base(settings)
    {
        sub = new Lazy<TSub>(() => ServiceProvider.GetRequiredService<TSub>());
    }

    public TSub Sub => sub.Value;
}


public abstract class SaSubFixture<TSub>() : SaFixture<TSub, object>(new())
     where TSub : notnull
{
}