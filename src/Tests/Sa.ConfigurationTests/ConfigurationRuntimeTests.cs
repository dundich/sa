using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sa.Configuration;
using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

/// <summary>
/// The dynamic layer and its reloader: only the dynamic providers are refreshed, the derived values
/// (aliases, placeholders) follow, and nothing runs in the background unless it is switched on.
/// </summary>
public class ConfigurationRuntimeTests
{
    private static SecretOptions Secrets(KeyAliasOptions? aliases = null)
        => new()
        {
            LoadDotEnv = false,
            Encryption = new SecretEncryptionOptions { Enabled = false },
            KeyAliases = aliases ?? new KeyAliasOptions(),
        };

    [Fact]
    public async Task ReloadDynamic_RefreshesOnlyTheDynamicLayer_AndRecomputesThePass()
    {
        var staticData = new Dictionary<string, string?> { ["static"] = "{token}" };
        var dynamicData = new Dictionary<string, string?> { ["auth_base_url"] = "first" };

        var builder = new ConfigurationBuilder();
        builder.Add(new SnapshotSource(staticData));
        builder.AddSaConfiguration(sa => sa.UseSecrets(Secrets(new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
        })));

        DynamicSource dynamicSource = new(dynamicData);
        builder.Add(dynamicSource);

        IConfigurationRoot configuration = builder.Build();
        SaConfigurationRuntime runtime = SaConfigurationRuntime.GetOrAdd(builder);

        Assert.Equal("first", configuration["sa:scope:api:auth:base_url"]);

        // Act — the database changes; only the dynamic layer is refreshed.
        int loadsBefore = dynamicSource.Primary!.Reloads;
        dynamicData["auth_base_url"] = "second";
        await runtime.ReloadDynamicAsync(TestContext.Current.CancellationToken);

        // Assert — the alias is recomputed from the fresh dynamic value (the pass used to keep a
        // stale snapshot), and the dynamic provider was the only outer provider reloaded.
        Assert.Equal("second", configuration["sa:scope:api:auth:base_url"]);
        Assert.Equal(loadsBefore + 1, dynamicSource.Primary.Reloads);
        Assert.Equal(1, runtime.DynamicSourceCount);
    }

    [Fact]
    public async Task ReloadDynamic_LeavesTheStaticProvidersAlone()
    {
        var staticData = new Dictionary<string, string?> { ["value"] = "static" };
        var dynamicData = new Dictionary<string, string?> { ["value"] = "dynamic" };

        var builder = new ConfigurationBuilder();
        builder.Add(new SnapshotSource(staticData));
        builder.AddSaConfiguration(sa => sa.UseSecrets(Secrets()));

        DynamicSource dynamicSource = new(dynamicData);
        builder.Add(dynamicSource);

        IConfigurationRoot configuration = builder.Build();
        SaConfigurationRuntime runtime = SaConfigurationRuntime.GetOrAdd(builder);

        int loadsBefore = dynamicSource.Primary!.Reloads;
        dynamicData["value"] = "dynamic-2";
        await runtime.ReloadDynamicAsync(TestContext.Current.CancellationToken);

        // The outer static provider is a distinct instance from the one the pass builds, and it was
        // never reloaded; only its own file watcher or an explicit root reload does that.
        Assert.Equal(loadsBefore + 1, dynamicSource.Primary.Reloads);
        Assert.Equal("dynamic-2", configuration["value"]);
    }

    [Fact]
    public void AddSaConfigurationRuntime_RegistersNoHostedServices_ByDefault()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(sa => sa.UseSecrets(Secrets()));

        var services = new ServiceCollection();
        services.AddSaConfigurationRuntime(builder);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ISaConfigurationReloader));
    }

    [Fact]
    public void AddSaConfigurationRuntime_RegistersExactlyOneHostedService_WhenPollingIsEnabled()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(sa => sa.UseSecrets(Secrets()));

        var services = new ServiceCollection();
        services.AddSaConfigurationRuntime(builder, options => options.DynamicReload = true);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddSaConfigurationRuntime_RegistersAOneShotHook_WhenInitialLoadIsHosted()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(sa => sa.UseSecrets(Secrets()));

        var services = new ServiceCollection();
        services.AddSaConfigurationRuntime(builder, options => options.InitialLoad = SaDynamicInitialLoad.Hosted);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task EnsureInitialized_RunsLateBoundInitializers_Once()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(sa => sa.UseSecrets(Secrets()));
        builder.Add(new DynamicSource(new Dictionary<string, string?>()));

        var services = new ServiceCollection();
        services.AddSaConfigurationRuntime(builder);

        using ServiceProvider provider = services.BuildServiceProvider();
        ISaConfigurationReloader reloader = provider.GetRequiredService<ISaConfigurationReloader>();

        SaConfigurationRuntime runtime = SaConfigurationRuntime.GetOrAdd(builder);
        int initialized = 0;
        runtime.RegisterInitializer((_, _) =>
        {
            initialized++;
            return ValueTask.CompletedTask;
        });

        await reloader.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        await reloader.EnsureInitializedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, initialized);
    }

    [Fact]
    public async Task EnsureInitialized_Fails_WhenALateBoundInitializerHasNoServiceProvider()
    {
        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(sa => sa.UseSecrets(Secrets()));
        builder.Add(new DynamicSource(new Dictionary<string, string?>()));

        SaConfigurationRuntime runtime = SaConfigurationRuntime.GetOrAdd(builder);
        runtime.RegisterInitializer((_, _) => ValueTask.CompletedTask);

        // No AddSaConfigurationRuntime was called, so no container is attached.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.EnsureInitializedAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public void UseDynamic_RejectsASourceThatIsNotMarkedDynamic()
    {
        var builder = new ConfigurationBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddSaConfiguration(
            sa => sa.Use(new SnapshotSource(new Dictionary<string, string?>()), SaConfigurationLayer.Dynamic)));
    }

    /// <summary>Builds a fresh provider on every build, like a JSON source does.</summary>
    private sealed class SnapshotSource(Dictionary<string, string?> data) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new SnapshotProvider(data);
    }

    private sealed class SnapshotProvider(Dictionary<string, string?> data) : ConfigurationProvider
    {
        public override void Load()
            => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The dynamic layer: a source that builds a fresh provider per build (like a database or JSON
    /// source), registers it, and keeps the first instance for the test to observe.
    /// </summary>
    private sealed class DynamicSource(Dictionary<string, string?> data) : ISaDynamicConfigurationSource
    {
        public MutableProvider? Primary { get; private set; }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            MutableProvider provider = new(data);
            Primary ??= provider;

            SaConfigurationRuntime.GetOrAdd(builder).RegisterDynamicProvider(provider);

            return provider;
        }
    }

    private sealed class MutableProvider(Dictionary<string, string?> data) : ConfigurationProvider
    {
        public int Reloads { get; private set; }

        public override void Load()
        {
            Reloads++;
            Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
        }
    }
}
