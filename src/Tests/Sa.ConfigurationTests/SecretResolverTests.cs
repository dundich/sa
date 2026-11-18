using Microsoft.Extensions.Configuration;
using Sa.Configuration;
using Sa.Configuration.SecretStore;
using Sa.Configuration.SecretStore.Stores;

namespace Sa.ConfigurationTests;

public class SecretResolverTests
{
    private static InMemorySecretStore Store() => new(new Dictionary<string, string?>
    {
        ["present"] = "from-store",
    });

    [Fact]
    public void PopulateSecrets_UsesResolver_WhenStoresMiss()
    {
        var secrets = new Secrets(
            new SecretResolutionOptions
            {
                Resolver = key => key == "dynamic" ? "from-resolver" : null,
            },
            Store());

        Assert.Equal("from-resolver", secrets.PopulateSecrets("{{dynamic}}"));
    }

    [Fact]
    public void PopulateSecrets_DoesNotCallResolver_WhenStoreHasTheKey()
    {
        bool called = false;

        var secrets = new Secrets(
            new SecretResolutionOptions
            {
                Resolver = _ =>
                {
                    called = true;
                    return null;
                },
            },
            Store());

        Assert.Equal("from-store", secrets.PopulateSecrets("{{present}}"));
        Assert.False(called);
    }

    [Fact]
    public void PopulateSecrets_Throws_WhenResolverReturnsNull_AndPlaceholderIsNotOptional()
    {
        var misses = new List<SecretMiss>();

        var secrets = new Secrets(
            new SecretResolutionOptions
            {
                Resolver = _ => null,
                OnUnresolvedSecret = misses.Add,
            },
            Store());

        Assert.Throws<ArgumentException>(() => secrets.PopulateSecrets("{{missing}}"));

        SecretMiss miss = Assert.Single(misses);
        Assert.Equal("missing", miss.Key);
        Assert.False(miss.IsOptional);
    }

    [Fact]
    public void PopulateSecrets_ReturnsNull_WhenResolverReturnsNull_AndPlaceholderIsOptional()
    {
        var misses = new List<SecretMiss>();

        var secrets = new Secrets(
            new SecretResolutionOptions
            {
                Resolver = _ => null,
                OnUnresolvedSecret = misses.Add,
            },
            Store());

        Assert.Null(secrets.PopulateSecrets("{{?missing}}"));

        SecretMiss miss = Assert.Single(misses);
        Assert.Equal("missing", miss.Key);
        Assert.True(miss.IsOptional);
    }

    [Fact]
    public void PopulateSecrets_ReturnsNull_WhenResolverReturnsNull_AndFlagIsTrue()
    {
        var misses = new List<SecretMiss>();

        var secrets = new Secrets(
            new SecretResolutionOptions
            {
                Resolver = _ => null,
                OnUnresolvedSecret = misses.Add,
            },
            Store());

        Assert.Null(secrets.PopulateSecrets("{{missing}}", returnNullIfSecretNotFound: true));

        SecretMiss miss = Assert.Single(misses);
        Assert.Equal("missing", miss.Key);
        Assert.True(miss.IsOptional);
    }

    [Fact]
    public void PopulateSecrets_DoesNotReportMiss_WhenResolverResolves()
    {
        var misses = new List<SecretMiss>();

        var secrets = new Secrets(
            new SecretResolutionOptions
            {
                Resolver = _ => "from-resolver",
                OnUnresolvedSecret = misses.Add,
            },
            Store());

        Assert.Equal("from-resolver", secrets.PopulateSecrets("{{missing}}"));
        Assert.Empty(misses);
    }

    [Fact]
    public void GetSecret_UsesResolver()
    {
        var secrets = new Secrets(
            new SecretResolutionOptions
            {
                Resolver = key => key == "dynamic" ? "from-resolver" : null,
            },
            Store());

        Assert.Equal("from-resolver", secrets.GetSecret("dynamic"));
        Assert.Null(secrets.GetSecret("absent"));
    }

    [Fact]
    public void CreateDefault_PassesResolutionOptions()
    {
        string key = "sa_test_" + Guid.NewGuid().ToString("N");

        var secrets = Secrets.CreateDefault(new SecretOptions
        {
            FileName = "does-not-exist.txt",
            LoadDotEnv = false,
            Resolution = new SecretResolutionOptions
            {
                Resolver = candidate => candidate == key ? "from-resolver" : null,
            },
        });

        Assert.Equal("from-resolver", secrets.PopulateSecrets("{{" + key + "}}"));
    }

    [Fact]
    public void AddSaConfiguration_UsesResolverFromSecretOptions()
    {
        string key = "sa_test_" + Guid.NewGuid().ToString("N");

        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resolved"] = "{{" + key + "}}",
            });

        builder.AddSaConfiguration(new SecretOptions
        {
            FileName = "does-not-exist.txt",
            LoadDotEnv = false,
            Resolution = new SecretResolutionOptions
            {
                Resolver = candidate => candidate == key ? "from-resolver" : null,
            },
        });

        IConfigurationRoot configuration = builder.Build();

        Assert.Equal("from-resolver", configuration["Resolved"]);
    }
}
