using Microsoft.Extensions.Configuration;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

/// <summary>
/// The per-client overlay: <c>sa:clients:{client}:{key}</c> laid over the base values, with the
/// computed-key spelling, isolation between clients and per-client key aliases.
/// </summary>
public class ClientsConfigurationTests
{
    private static Dictionary<string, string?> Data() => new()
    {
        ["theme"] = "dark",
        ["language"] = "en",
        ["sa:clients:acme:theme"] = "light",
        ["sa:clients:acme:notifications"] = "disabled",
        ["sa:clients:beta:language"] = "de",
    };

    /// <summary>
    /// A client key is just a path, so it can be computed instead of hardcoded — the same shape the
    /// database query produces with <c>ClientColumn</c>.
    /// </summary>
    private static string ClientKey(string client, string setting)
        => $"{ClientConfiguration.DefaultSectionPath}:{client}:{setting}";

    [Fact]
    public void ClientValue_OverridesTheBaseValue()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Data()).Build();

        IConfiguration acme = configuration.ForClient("acme");

        Assert.Equal("light", acme["theme"]);
        Assert.Equal("en", acme["language"]);            // not overridden: base value
        Assert.Equal("disabled", acme["notifications"]);
    }

    [Fact]
    public void ComputedClientKey_ReadsTheSameValueAsTheOverlay()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Data()).Build();

        // Both spellings address one setting: the explicit path and the computed one.
        Assert.Equal("light", configuration[ClientKey("acme", "theme")]);
        Assert.Equal("light", configuration.ForClient("acme")["theme"]);

        // The raw prefixed key only exists where the client actually overrides it…
        Assert.Null(configuration[ClientKey("beta", "theme")]);

        // …while the overlay falls back to the base value, never inventing one.
        Assert.Equal("dark", configuration.ForClient("beta")["theme"]);
    }

    [Fact]
    public void Clients_AreIsolatedFromEachOther()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Data()).Build();

        IConfiguration acme = configuration.ForClient("acme");
        IConfiguration beta = configuration.ForClient("beta");

        Assert.Equal("light", acme["theme"]);
        Assert.Equal("dark", beta["theme"]);      // acme's override must not leak
        Assert.Equal("de", beta["language"]);
        Assert.Equal("en", acme["language"]);
    }

    [Fact]
    public void UnknownClient_ReadsTheBaseValues()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Data()).Build();

        IConfiguration unknown = configuration.ForClient("nope");

        Assert.Equal("dark", unknown["theme"]);
        Assert.Equal("en", unknown["language"]);
    }

    [Fact]
    public void ClientSections_MergeBaseAndClientChildren()
    {
        var data = new Dictionary<string, string?>
        {
            ["db:connection"] = "base-conn",
            ["db:timeout"] = "30",
            ["sa:clients:acme:db:connection"] = "acme-conn",
        };

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        IConfigurationSection db = configuration.ForClient("acme").GetSection("db");

        Assert.Equal("acme-conn", db["connection"]);
        Assert.Equal("30", db["timeout"]);
        Assert.Equal("connection", db.GetSection("connection").Key);
    }

    [Fact]
    public void ClientNames_AreDiscoveredFromTheOverrides()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Data()).Build();

        Assert.Equal(["acme", "beta"], configuration.GetClientNames().Order());

        var factory = new ClientConfigurationFactory(configuration);

        Assert.Equal(["acme", "beta"], factory.Clients.Order());
        Assert.True(factory.TryGetClient("acme", out _));
        Assert.False(factory.TryGetClient("nope", out _));
        Assert.Equal("light", factory.GetClient("acme")["theme"]);
    }

    [Fact]
    public void EnumeratingAClient_ExposesItsValuesWithoutThePrefix()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(Data()).Build();

        Dictionary<string, string?> acme = configuration.ForClient("acme")
            .AsEnumerable()
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("light", acme["theme"]);
        Assert.Equal("en", acme["language"]);
        Assert.DoesNotContain(acme.Keys, key => key.StartsWith("sa:clients:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aliases_ApplyInsideEachClientPrefix_WithoutCrossingIt()
    {
        var data = new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://base.example.com",
            ["sa:clients:acme:auth_base_url"] = "https://acme.example.com",
        };

        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(data);
        builder.AddSaConfiguration(sa =>
        {
            sa.UseClients();
            sa.UseSecrets(new SecretOptions
            {
                LoadDotEnv = false,
                Encryption = new SecretEncryptionOptions { Enabled = false },
                KeyAliases = new KeyAliasOptions
                {
                    KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
                },
            });
        });

        IConfiguration configuration = builder.Build();

        Assert.Equal("https://base.example.com", configuration["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://acme.example.com", configuration["sa:clients:acme:sa:scope:api:auth:base_url"]);
        Assert.Equal("https://acme.example.com", configuration.ForClient("acme")["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://base.example.com", configuration.ForClient("beta")["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void ClientsSection_CanBeCustomised()
    {
        var data = new Dictionary<string, string?>
        {
            ["theme"] = "dark",
            ["tenants:acme:theme"] = "light",
        };

        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(data);
        builder.AddSaConfiguration(sa => sa.UseClients("tenants"));

        IConfiguration configuration = builder.Build();

        Assert.Equal("light", configuration.ForClient("acme", "tenants")["theme"]);
        Assert.Equal(["acme"], configuration.GetClientNames("tenants"));
    }
}
