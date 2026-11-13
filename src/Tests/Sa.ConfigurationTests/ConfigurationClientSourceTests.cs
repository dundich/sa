using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

/// <summary>
/// The no-database client provider: the children of a configuration section are the clients, and
/// each client's own section supplies rows with the same key names as the core configuration.
/// </summary>
public class ConfigurationClientSourceTests
{
    private static IConfiguration Core() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["s3_url"] = "A",
            ["theme"] = "dark",
            ["language"] = "en",
            ["clients:acme:s3_url"] = "B1",
            ["clients:acme:theme"] = "light",
            ["clients:beta:s3_url"] = "C",
            ["clients:beta:language"] = "de",
        })
        .Build();

    [Fact]
    public void UseConfiguration_ReadsClientsFromTheSection()
    {
        IConfiguration configuration = Core();

        var registration = new SaClientConfigurationRegistration();
        registration.UseConfiguration(configuration);

        using var clients = (ClientConfigurations)registration.Build(
            configuration,
            new ServiceCollection().BuildServiceProvider());

        Assert.Equal(["acme", "beta"], clients.Clients.Order());

        // Client values win, missing ones fall back to the core value, and the section prefix
        // never leaks into the client's own key space.
        Assert.Equal("B1", clients.GetClient("acme")["s3_url"]);
        Assert.Equal("light", clients.GetClient("acme")["theme"]);
        Assert.Equal("en", clients.GetClient("acme")["language"]);
        Assert.Equal("C", clients.GetClient("beta")["s3_url"]);
        Assert.Equal("de", clients.GetClient("beta")["language"]);
        Assert.Equal("dark", clients.GetClient("beta")["theme"]);

        // The core is untouched and the section itself is not a setting of a client.
        Assert.Equal("A", configuration["s3_url"]);
        Assert.Null(clients.GetClient("acme")["clients"]);
    }

    [Fact]
    public void UnknownClient_ReadsTheCoreValues()
    {
        IConfiguration configuration = Core();

        var registration = new SaClientConfigurationRegistration();
        registration.UseConfiguration(configuration);

        using var clients = (ClientConfigurations)registration.Build(
            configuration,
            new ServiceCollection().BuildServiceProvider());

        Assert.Equal("A", clients.GetClient("gamma")["s3_url"]);
        Assert.False(clients.TryGet("gamma", out _));
    }

    [Fact]
    public void CustomSectionPath_IsHonoured()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["s3_url"] = "A",
                ["tenants:acme:s3_url"] = "B1",
            })
            .Build();

        var registration = new SaClientConfigurationRegistration();
        registration.UseConfiguration(
            configuration,
            new ConfigurationClientSourceOptions { SectionPath = "tenants" });

        using var clients = (ClientConfigurations)registration.Build(
            configuration,
            new ServiceCollection().BuildServiceProvider());

        Assert.Equal(["acme"], clients.Clients);
        Assert.Equal("B1", clients.GetClient("acme")["s3_url"]);
    }

    [Fact]
    public void NestedKeys_AreStoredWithTheCoreSeparator()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["s3_url"] = "A",
                ["clients:acme:db:host"] = "acme-db",
                ["clients:acme:db:port"] = "5432",
            })
            .Build();

        var registration = new SaClientConfigurationRegistration();
        registration.UseConfiguration(configuration);

        using var clients = (ClientConfigurations)registration.Build(
            configuration,
            new ServiceCollection().BuildServiceProvider());

        Assert.Equal("acme-db", clients.GetClient("acme").GetSection("db")["host"]);
        Assert.Equal("5432", clients.GetClient("acme").GetSection("db")["port"]);
    }

    [Fact]
    public async Task Reload_SeesNewClients_AndFreshValues()
    {
        MemoryConfigurationProvider data = new(new MemoryConfigurationSource());
        data.Set("s3_url", "A");
        data.Set("clients:acme:s3_url", "B1");

        IConfigurationRoot configuration = new ConfigurationRoot([data]);

        var registration = new SaClientConfigurationRegistration();
        registration.UseConfiguration(configuration);

        using var clients = (ClientConfigurations)registration.Build(
            configuration,
            new ServiceCollection().BuildServiceProvider());

        Assert.Equal(["acme"], clients.Clients);
        Assert.Equal("B1", clients.GetClient("acme")["s3_url"]);

        data.Set("clients:acme:s3_url", "B2");
        data.Set("clients:beta:s3_url", "C");

        await clients.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["acme", "beta"], clients.Clients.Order());
        Assert.Equal("B2", clients.GetClient("acme")["s3_url"]);
        Assert.Equal("C", clients.GetClient("beta")["s3_url"]);
    }

    [Fact]
    public void DependencyInjection_RegistersTheRegistry_AndAKeyedConfiguration()
    {
        IConfiguration configuration = Core();

        var services = new ServiceCollection();
        services.AddSaClientConfigurations(
            configuration,
            clients => clients.UseConfiguration(configuration));

        using ServiceProvider provider = services.BuildServiceProvider();

        IClientConfigurations clients = provider.GetRequiredService<IClientConfigurations>();

        Assert.Equal("B1", clients.GetClient("acme")["s3_url"]);
        Assert.Equal("B1", provider.GetRequiredKeyedService<IConfiguration>("acme")["s3_url"]);
        Assert.Equal("A", provider.GetRequiredKeyedService<IConfiguration>("unknown")["s3_url"]);
    }

    [Fact]
    public void Pass_AppliesToSectionRows_WithoutTouchingTheCore()
    {
        ConfigurationManager configuration = new();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["s3_url"] = "A",
            ["auth_base_url"] = "https://core.example.com",
            ["clients:acme:s3_url"] = "{{zz_client_pw}}",
            ["clients:acme:auth_base_url"] = "https://acme.example.com",
        });

        configuration.AddSaConfiguration(new SecretOptions
        {
            Args = [],
            LoadDotEnv = false,
            Encryption = new SecretEncryptionOptions { Enabled = false },
            Resolution = new SecretResolutionOptions
            {
                Resolver = key => key == "zz_client_pw" ? "s3cr3t" : null,
            },
            KeyAliases = new KeyAliasOptions
            {
                KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
            },
        });

        var registration = new SaClientConfigurationRegistration();
        registration.UseConfiguration(configuration);

        using var clients = (ClientConfigurations)registration.Build(
            configuration,
            new ServiceCollection().BuildServiceProvider());

        // The section's rows go through the same pass: the placeholder is resolved and the flat alias
        // key inside the client's section fills the canonical path for that client.
        Assert.Equal("s3cr3t", clients.GetClient("acme")["s3_url"]);
        Assert.Equal("https://acme.example.com", clients.GetClient("acme")["sa:scope:api:auth:base_url"]);

        // The core keeps its own values and its own derived alias.
        Assert.Equal("A", configuration["s3_url"]);
        Assert.Equal("https://core.example.com", configuration["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void OtherClientsRows_AreHiddenFromAView()
    {
        IConfiguration configuration = Core();

        var registration = new SaClientConfigurationRegistration();
        registration.UseConfiguration(configuration);

        using var clients = (ClientConfigurations)registration.Build(
            configuration,
            new ServiceCollection().BuildServiceProvider());

        IConfiguration acme = clients.GetClient("acme");

        // The section is meta-data of the overlay: no client reads it — its own rows or another
        // client's — through the core fallback.
        Assert.Null(acme["clients"]);
        Assert.Null(acme["clients:acme:s3_url"]);
        Assert.Null(acme["clients:beta:s3_url"]);
        Assert.Null(acme.GetSection("clients")["beta:s3_url"]);

        // The client's own settings and the shared core ones are still there — only the section
        // is gone.
        Dictionary<string, string?> values = acme.AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("B1", values["s3_url"]);
        Assert.Equal("light", values["theme"]);
        Assert.Equal("en", values["language"]);
        Assert.Equal(["language", "s3_url", "theme"], values.Keys.Order());
    }

    [Fact]
    public void BlankSectionPath_IsRejected()
    {
        var registration = new SaClientConfigurationRegistration();

        Assert.Throws<ArgumentException>(() => registration.UseConfiguration(
            Core(),
            new ConfigurationClientSourceOptions { SectionPath = " " }));
    }
}
