using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostProcessing;
using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

/// <summary>
/// The client strategy: the core configuration keeps the shared values, every client has its own
/// overlay with the same key names, and a provider returning flat rows feeds all of them.
/// </summary>
public class ClientsConfigurationTests
{
    private static IConfiguration Core() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["s3_url"] = "A",
            ["theme"] = "dark",
            ["db:host"] = "core-db",
            ["db:port"] = "5432",
        })
        .Build();

    private static ClientConfigurations Registry(IConfiguration core, ProviderRows rows)
    {
        var registration = new SaClientConfigurationRegistration();
        registration.Add<ProviderRows>((source, _) =>
            ValueTask.FromResult<IReadOnlyList<ClientSetting>>([.. source.Items]));

        return (ClientConfigurations)registration.Build(
            core,
            new ServiceCollection().AddSingleton(rows).BuildServiceProvider());
    }

    [Fact]
    public void ClientValue_OverridesTheCoreValue_ButTheCoreStaysTheSame()
    {
        IConfiguration core = Core();
        ProviderRows rows = new()
        {
            Items =
            [
                new("client1", "s3_url", "B1"),
                new("client2", "s3_url", "C"),
            ],
        };

        ClientConfigurations clients = Registry(core, rows);

        // The core is untouched: the clients are separate roots, not prefixed values in it.
        Assert.Equal("A", core["s3_url"]);

        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("C", clients.GetClient("client2")["s3_url"]);

        // A missing client value falls back to the core one and is never invented.
        Assert.Equal("dark", clients.GetClient("client1")["theme"]);
        Assert.Equal("dark", clients.GetClient("client2")["theme"]);
    }

    [Fact]
    public void UnknownClient_ReadsTheCoreValues()
    {
        IConfiguration core = Core();
        ClientConfigurations clients = Registry(core, new ProviderRows());

        Assert.Equal("A", clients.GetClient("nope")["s3_url"]);
        Assert.False(clients.TryGet("nope", out _));
    }

    [Fact]
    public void Clients_AreIsolatedFromEachOther()
    {
        IConfiguration core = Core();
        ProviderRows rows = new()
        {
            Items =
            [
                new("client1", "s3_url", "B1"),
                new("client2", "s3_url", "C"),
            ],
        };

        ClientConfigurations clients = Registry(core, rows);

        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("C", clients.GetClient("client2")["s3_url"]);
        Assert.Equal("A", clients.GetClient("client-unregistered")["s3_url"]);
    }

    [Fact]
    public void ClientSections_MergeCoreAndClientChildren()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "db:host", "client1-db")] };

        ClientConfigurations clients = Registry(core, rows);

        IConfigurationSection db = clients.GetClient("client1").GetSection("db");

        Assert.Equal("client1-db", db["host"]);
        Assert.Equal("5432", db["port"]);
        Assert.Equal("host", db.GetSection("host").Key);
    }

    [Fact]
    public void EnumeratingAClient_ExposesItsOverlayOverTheCore()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        ClientConfigurations clients = Registry(core, rows);

        Dictionary<string, string?> values = clients.GetClient("client1")
            .AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("B1", values["s3_url"]);
        Assert.Equal("dark", values["theme"]);
    }

    [Fact]
    public async Task AttachAsync_AddsAnOverlayAtRuntime_AndRemoveRestoresTheCore()
    {
        IConfiguration core = Core();
        var registry = new ClientConfigurations(core);

        await registry.AttachAsync(
            "runtime",
            new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?> { ["s3_url"] = "late" },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("late", registry.GetClient("runtime")["s3_url"]);
        Assert.True(registry.TryGet("runtime", out _));
        Assert.Contains("runtime", registry.Clients);

        Assert.True(await registry.RemoveAsync("runtime", TestContext.Current.CancellationToken));
        Assert.Equal("A", registry.GetClient("runtime")["s3_url"]);
    }

    [Fact]
    public async Task Reload_DiscoversAClientAddedToTheProvider_AndRefreshesValues()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        ClientConfigurations clients = Registry(core, rows);

        Assert.Equal(["client1"], clients.Clients);
        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);

        rows.Items =
        [
            new("client1", "s3_url", "B2"),
            new("client2", "s3_url", "C"),
        ];

        await clients.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["client1", "client2"], clients.Clients.Order());
        Assert.Equal("B2", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("C", clients.GetClient("client2")["s3_url"]);
    }

    [Fact]
    public async Task Reload_DropsAClientTheProviderNoLongerReturns()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        ClientConfigurations clients = Registry(core, rows);

        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);

        rows.Items = [];

        await clients.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(clients.Clients);
        Assert.Equal("A", clients.GetClient("client1")["s3_url"]);
    }

    [Fact]
    public async Task Views_AreLive_AndFollowAReload()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        ClientConfigurations clients = Registry(core, rows);
        IConfiguration view = clients.GetClient("client1");

        rows.Items = [new("client1", "s3_url", "B2")];
        await clients.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("B2", view["s3_url"]);
    }

    [Fact]
    public void ThrowForUnknownClient_CanBeEnabled()
    {
        var registration = new SaClientConfigurationRegistration();
        registration.Configure(options => options.ThrowForUnknownClient = true);

        IClientConfigurations clients = registration.Build(
            Core(),
            new ServiceCollection().BuildServiceProvider());

        Assert.Throws<InvalidOperationException>(() => clients.GetClient("nope"));
    }

    [Fact]
    public void TryGet_ReportsAnUnknownClient_WithoutThrowing_WhenThrowForUnknownClientIsSet()
    {
        var registration = new SaClientConfigurationRegistration();
        registration.Configure(options => options.ThrowForUnknownClient = true);

        IClientConfigurations clients = registration.Build(
            Core(),
            new ServiceCollection().BuildServiceProvider());

        // TryGet reports, it does not throw — even though GetClient does for the same id.
        Assert.False(clients.TryGet("nope", out IConfiguration view));
        Assert.Equal("A", view["s3_url"]);
        Assert.Throws<InvalidOperationException>(() => clients.GetClient("nope"));
    }

    [Fact]
    public async Task RemoveAsync_ReportsFalse_ForAProviderBackedClient()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        using ClientConfigurations clients = Registry(core, rows);

        // The provider still reports the client, so there is nothing to remove: the call says so
        // instead of pretending the overlay is gone.
        Assert.False(await clients.RemoveAsync("client1", TestContext.Current.CancellationToken));

        Assert.True(clients.TryGet("client1", out _));
        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);
    }

    [Fact]
    public async Task AttachAsync_DisposesTheReplacedOverlay_NotTheOneItAttaches()
    {
        IConfiguration core = Core();
        using var registry = new ClientConfigurations(core);
        DisposalCounter counter = new();
        IConfiguration view = registry.GetClient("runtime");

        await registry.AttachAsync("runtime", new CountingSource(counter), TestContext.Current.CancellationToken);

        // The root that was just attached is live: nothing has been replaced yet.
        Assert.Equal(0, counter.Disposed);
        Assert.Equal("attached", view["s3_url"]);

        await registry.AttachAsync("runtime", new CountingSource(counter), TestContext.Current.CancellationToken);

        // Only the replaced root is disposed; the new one keeps serving the view.
        Assert.Equal(1, counter.Disposed);
        Assert.Equal("attached", view["s3_url"]);

        Assert.True(await registry.RemoveAsync("runtime", TestContext.Current.CancellationToken));
        Assert.Equal(2, counter.Disposed);

        // The view obtained before the attach follows the registry: the overlay is gone.
        Assert.Equal("A", view["s3_url"]);
    }

    [Fact]
    public void DependencyInjection_ResolvesTheRegistry_AndAKeyedConfiguration()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        var services = new ServiceCollection();
        services.AddSingleton(core);
        services.AddSingleton(rows);
        services.AddSaClientConfigurations(core, clients =>
            clients.Add<ProviderRows>((source, _) =>
                ValueTask.FromResult<IReadOnlyList<ClientSetting>>([.. source.Items])));

        using ServiceProvider provider = services.BuildServiceProvider();

        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("B1", provider.GetRequiredKeyedService<IConfiguration>("client1")["s3_url"]);
        Assert.Equal("A", provider.GetRequiredKeyedService<IConfiguration>("unknown")["s3_url"]);
    }

    [Fact]
    public void Provider_ResolvesUpToThreeDependenciesFromTheContainer()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        var services = new ServiceCollection();
        services.AddSingleton(rows);
        services.AddSingleton(new ProviderRowsPrefix("B"));
        services.AddSingleton(new ProviderRowsSuffix("1"));

        services.AddSaClientConfigurations(core, clients =>
            clients.Add<ProviderRows, ProviderRowsPrefix, ProviderRowsSuffix>(
                (source, prefix, suffix, _) =>
                    ValueTask.FromResult<IReadOnlyList<ClientSetting>>(
                        [new("client1", "s3_url", prefix.Value + suffix.Value)])));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(
            "B1",
            provider.GetRequiredKeyedService<IConfiguration>("client1")["s3_url"]);
    }

    [Fact]
    public void SeveralProviders_AreMergedPerClient()
    {
        IConfiguration core = Core();

        var registration = new SaClientConfigurationRegistration();
        registration.Configure(options => options.EagerLoad = true);
        registration.Add<ProviderRows>((_, _) =>
            ValueTask.FromResult<IReadOnlyList<ClientSetting>>([new("client1", "s3_url", "B1")]));
        registration.Add<ProviderRowsPrefix>((prefix, _) =>
            ValueTask.FromResult<IReadOnlyList<ClientSetting>>([new("client2", "s3_url", prefix.Value)]));

        using var clients = (ClientConfigurations)registration.Build(
            core,
            new ServiceCollection()
                .AddSingleton(new ProviderRows())
                .AddSingleton(new ProviderRowsPrefix("C"))
                .BuildServiceProvider());

        Assert.Equal(["client1", "client2"], clients.Clients.Order());
        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("C", clients.GetClient("client2")["s3_url"]);
    }

    [Fact]
    public void Aliases_AreResolvedInsideEachClientOverlay_WithoutCrossingIt()
    {
        (ConfigurationManager core, _) = AliasedCore("https://core.example.com");
        ProviderRows rows = new() { Items = [new("client1", "auth_base_url", "https://client1.example.com")] };

        using ClientConfigurations clients = Registry(core, rows);

        // The alias pass is not core-only: the client's flat key fills the canonical path for that
        // client, and the core keeps its own value.
        Assert.Equal("https://core.example.com", core["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://client1.example.com", clients.GetClient("client1")["sa:scope:api:auth:base_url"]);

        // A client without the flat key reads the core value, not another client's.
        Assert.Equal("https://core.example.com", clients.GetClient("client2")["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void Aliases_DoNotFreezeTheCoreValueIntoTheClientOverlay()
    {
        (ConfigurationManager core, MutableSource source) = AliasedCore("https://core.example.com");
        ProviderRows rows = new() { Items = [new("client2", "theme", "light")] };

        using ClientConfigurations clients = Registry(core, rows);
        Assert.Equal("https://core.example.com", clients.GetClient("client2")["sa:scope:api:auth:base_url"]);

        // The core changes without a second client load: the overlay must follow the core instead of
        // serving a copy the transform derived while the rows were built.
        source.Data["auth_base_url"] = "https://core2.example.com";
        ((IConfigurationRoot)core).Reload();

        Assert.Equal("https://core2.example.com", core["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://core2.example.com", clients.GetClient("client2")["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void Placeholders_AreSubstitutedInClientValues()
    {
        ConfigurationManager core = new();
        core.AddInMemoryCollection(new Dictionary<string, string?> { ["s3_url"] = "A" });
        core.AddSaConfiguration(PassOptions());

        ProviderRows rows = new() { Items = [new("client1", "s3_url", "{{zz_client_pw}}")] };

        using ClientConfigurations clients = Registry(core, rows);

        Assert.Equal("s3cr3t", clients.GetClient("client1")["s3_url"]);

        // The client's value never leaks into the core.
        Assert.Equal("A", core["s3_url"]);
    }

    [Fact]
    public void CustomTransforms_SeeTheClientValues()
    {
        ConfigurationManager core = new();
        core.AddInMemoryCollection(new Dictionary<string, string?> { ["s3_url"] = "A" });
        core.AddSaConfigurationTransform(new UppercaseSetting());

        ProviderRows rows = new() { Items = [new("client1", "s3_url", "b1")] };

        using ClientConfigurations clients = Registry(core, rows);

        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("A", core["s3_url"]);
    }

    [Fact]
    public void UnresolvedPlaceholders_BecomeBlank_InsteadOfLeakingOrFallingThrough()
    {
        ConfigurationManager core = new();
        core.AddInMemoryCollection(new Dictionary<string, string?> { ["s3_url"] = "A" });
        core.AddSaConfiguration(PassOptions());

        ProviderRows rows = new() { Items = [new("client1", "s3_url", "{{zz_absent_pw}}")] };

        using ClientConfigurations clients = Registry(core, rows);

        // Neither the literal placeholder nor the core value: the client's blank stays the client's.
        Assert.Equal(string.Empty, clients.GetClient("client1")["s3_url"]);
    }

    [Fact]
    public async Task Reload_ReappliesTheTransformsToTheNewRows()
    {
        (ConfigurationManager core, _) = AliasedCore("https://core.example.com");
        ProviderRows rows = new() { Items = [new("client1", "auth_base_url", "https://first.example.com")] };

        using ClientConfigurations clients = Registry(core, rows);
        Assert.Equal("https://first.example.com", clients.GetClient("client1")["sa:scope:api:auth:base_url"]);

        rows.Items =
        [
            new("client1", "auth_base_url", "https://second.example.com"),
            new("client1", "s3_url", "{{zz_client_pw}}"),
        ];

        await clients.ReloadAsync(TestContext.Current.CancellationToken);

        // The alias and the placeholder are recomputed from the fresh rows, not carried over from
        // the first build.
        Assert.Equal("https://second.example.com", clients.GetClient("client1")["sa:scope:api:auth:base_url"]);
        Assert.Equal("s3cr3t", clients.GetClient("client1")["s3_url"]);
    }

    [Fact]
    public async Task Transforms_AreAttachedThroughTheRuntime_ForAPlainConfigurationRoot()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["s3_url"] = "A",
            ["auth_base_url"] = "https://core.example.com",
        });
        builder.AddSaConfiguration(sa => sa.UseSecrets(PassOptions(new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        })));

        // A plain root, not a builder: the registry cannot find the pass itself here.
        IConfiguration core = builder.Build();

        ProviderRows rows = new()
        {
            Items =
            [
                new("client1", "auth_base_url", "https://client1.example.com"),
                new("client1", "s3_url", "{{zz_client_pw}}"),
            ],
        };

        var services = new ServiceCollection();
        services.AddSingleton(rows);
        services.AddSaConfigurationRuntime(builder);
        services.AddSaClientConfigurations(core, clients =>
            clients.Add<ProviderRows>((source, _) =>
                ValueTask.FromResult<IReadOnlyList<ClientSetting>>([.. source.Items])));

        using ServiceProvider provider = services.BuildServiceProvider();

        // Resolving the reloader is what attaches the registry — and the transform chain — to it.
        _ = provider.GetRequiredService<ISaConfigurationReloader>();
        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        Assert.Equal("https://client1.example.com", clients.GetClient("client1")["sa:scope:api:auth:base_url"]);
        Assert.Equal("s3cr3t", clients.GetClient("client1")["s3_url"]);
    }

    [Fact]
    public void KeyedConfiguration_CanBeInjectedIntoAKnownClientService()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        var services = new ServiceCollection();
        services.AddSingleton(rows);
        services.AddSingleton<KnownClientSettings>();
        services.AddSaClientConfigurations(core, clients =>
            clients.Add<ProviderRows>((source, _) =>
                ValueTask.FromResult<IReadOnlyList<ClientSetting>>([.. source.Items])));

        using ServiceProvider provider = services.BuildServiceProvider();

        // The known client is resolved by key in the constructor, no registry lookup in the body.
        Assert.Equal("B1", provider.GetRequiredService<KnownClientSettings>().S3Url);
    }

    [Fact]
    public async Task ReloadDynamic_RefreshesTheClientOverlays()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["s3_url"] = "A" });
        builder.AddSaConfiguration(sa => sa.UseSecrets(new SecretOptions
        {
            LoadDotEnv = false,
            Encryption = new SecretEncryptionOptions { Enabled = false },
        }));

        IConfiguration core = builder.Build();
        ProviderRows rows = new() { Items = [new("client1", "s3_url", "B1")] };

        var services = new ServiceCollection();
        services.AddSingleton(rows);
        services.AddSaConfigurationRuntime(builder);
        services.AddSaClientConfigurations(core, clients =>
            clients.Add<ProviderRows>((source, _) =>
                ValueTask.FromResult<IReadOnlyList<ClientSetting>>([.. source.Items])));

        using ServiceProvider provider = services.BuildServiceProvider();

        // The reloader is what attaches the registry to the runtime, so this ordering matters.
        ISaConfigurationReloader reloader = provider.GetRequiredService<ISaConfigurationReloader>();
        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        Assert.Equal("B1", clients.GetClient("client1")["s3_url"]);

        rows.Items = [new("client1", "s3_url", "B2"), new("client2", "s3_url", "C")];

        // One call drives the core and every client overlay: no second entry point exists.
        await reloader.ReloadDynamicAsync(TestContext.Current.CancellationToken);

        Assert.Equal("A", core["s3_url"]);
        Assert.Equal("B2", clients.GetClient("client1")["s3_url"]);
        Assert.Equal("C", clients.GetClient("client2")["s3_url"]);
        Assert.Equal(["client1", "client2"], clients.Clients.Order());
    }

    [Fact]
    public async Task ViewOfAClientDiscoveredAfterAReload_SeesTheClient()
    {
        IConfiguration core = Core();
        ProviderRows rows = new();

        using ClientConfigurations clients = Registry(core, rows);
        IConfiguration view = clients.GetClient("gamma");      // not known yet

        Assert.Equal("A", view["s3_url"]);

        rows.Items = [new("gamma", "s3_url", "G")];
        await clients.ReloadAsync(TestContext.Current.CancellationToken);

        // The reference obtained earlier follows the discovery, and agrees with a fresh lookup.
        Assert.Equal("G", view["s3_url"]);
        Assert.Equal("G", clients.GetClient("gamma")["s3_url"]);
        Assert.Contains("gamma", clients.Clients);
    }

    [Fact]
    public async Task ViewChangeToken_FiresOnAttachAndRemove()
    {
        using ClientConfigurations clients = new(Core());
        IConfiguration view = clients.GetClient("runtime");

        int attached = 0;
        view.GetReloadToken().RegisterChangeCallback(_ => attached++, null);

        await clients.AttachAsync("runtime", new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["s3_url"] = "late" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, attached);
        Assert.Equal("late", view["s3_url"]);

        int removed = 0;
        view.GetReloadToken().RegisterChangeCallback(_ => removed++, null);

        Assert.True(await clients.RemoveAsync("runtime", TestContext.Current.CancellationToken));

        Assert.Equal(1, removed);
        Assert.Equal("A", view["s3_url"]);
    }

    [Fact]
    public void EmptyRow_DelegatesToTheCore_UnlessSkipEmptyValuesIsOff()
    {
        IConfiguration core = Core();
        ProviderRows rows = new() { Items = [new("client1", "theme", "")] };

        // Default: a blank row must not blank the setting — the core value stays in effect, the
        // same rule SkipEmptyValues applies to the core database layer.
        using ClientConfigurations clients = Registry(core, rows);
        Assert.Equal("dark", clients.GetClient("client1")["theme"]);

        // Off: the blank is a value of its own and overrides the core one.
        var registration = new SaClientConfigurationRegistration();
        registration.Configure(options => options.SkipEmptyValues = false);
        registration.Add<ProviderRows>((source, _) =>
            ValueTask.FromResult<IReadOnlyList<ClientSetting>>([.. source.Items]));

        using ClientConfigurations strict = (ClientConfigurations)registration.Build(
            core,
            new ServiceCollection().AddSingleton(rows).BuildServiceProvider());

        Assert.Equal(string.Empty, strict.GetClient("client1")["theme"]);
    }

    private sealed class KnownClientSettings(
        [FromKeyedServices("client1")] IConfiguration configuration)
    {
        public string? S3Url => configuration["s3_url"];
    }

    /// <summary>
    /// A core configuration with the secret/alias pass registered, exposed as a
    /// <see cref="ConfigurationManager"/> so the registry can find the pass (it is both an
    /// <see cref="IConfiguration"/> and an <see cref="IConfigurationBuilder"/>).
    /// </summary>
    private static (ConfigurationManager Core, MutableSource Source) AliasedCore(string alias)
    {
        MutableSource source = new();
        source.Data["auth_base_url"] = alias;

        ConfigurationManager core = new();
        ((IConfigurationBuilder)core).Add(source);
        core.AddSaConfiguration(PassOptions(new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        }));

        return (core, source);
    }

    /// <summary>
    /// Options for the real pass with one secret resolved by the resolver hook — no file on disk and
    /// no dependence on the machine's environment.
    /// </summary>
    private static SecretOptions PassOptions(KeyAliasOptions? aliases = null)
        => new()
        {
            Args = [],
            LoadDotEnv = false,
            Encryption = new SecretEncryptionOptions { Enabled = false },
            Resolution = new SecretResolutionOptions
            {
                Resolver = key => key == "zz_client_pw" ? "s3cr3t" : null,
            },
            KeyAliases = aliases ?? new KeyAliasOptions(),
        };

    /// <summary>A static source whose data a test can change before reloading the core.</summary>
    private sealed class MutableSource : IConfigurationSource
    {
        public Dictionary<string, string?> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => new MutableProvider(Data);
    }

    private sealed class MutableProvider(Dictionary<string, string?> data) : ConfigurationProvider
    {
        public override void Load()
            => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A custom transform: upper-cases one setting, so its output is easy to observe.</summary>
    private sealed class UppercaseSetting : ISaConfigurationTransform
    {
        public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
        {
            foreach (KeyValuePair<string, string?> pair in configuration.Values)
            {
                if (pair.Value is not null
                    && string.Equals(pair.Key, "s3_url", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new(pair.Key, pair.Value.ToUpperInvariant());
                }
            }
        }
    }

    private sealed class DisposalCounter
    {
        public int Disposed { get; set; }
    }

    private sealed class CountingSource(DisposalCounter counter) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new CountingProvider(counter);
    }

    private sealed class CountingProvider(DisposalCounter counter) : ConfigurationProvider, IDisposable
    {
        public override void Load() => Data["s3_url"] = "attached";

        void IDisposable.Dispose() => counter.Disposed++;
    }

    private sealed class ProviderRows
    {
        public List<ClientSetting> Items { get; set; } = [];
    }

    private sealed class ProviderRowsPrefix(string value)
    {
        public string Value { get; } = value;
    }

    private sealed class ProviderRowsSuffix(string value)
    {
        public string Value { get; } = value;
    }
}
