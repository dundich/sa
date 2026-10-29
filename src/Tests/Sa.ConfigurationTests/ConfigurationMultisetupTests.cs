using Microsoft.Extensions.Configuration;
using Sa.Configuration;
using Sa.Configuration.CommandLine;
using Sa.Configuration.PostProcessing;
using Sa.Configuration.SecretStore;
using Sa.Configuration.SecretStore.Stores;

namespace Sa.ConfigurationTests;

/// <summary>
/// Repeated <c>AddSaConfiguration</c> registrations ("multisetup"): their alias maps accumulate
/// into one pass, instead of each call installing a pass that shadows the previous one.
/// </summary>
public class ConfigurationMultisetupTests
{
    private static SecretOptions Options(KeyAliasOptions? aliases = null, string? fileName = null)
        => new()
        {
            LoadDotEnv = false,
            Encryption = new SecretEncryptionOptions { Enabled = false },
            KeyAliases = aliases ?? new KeyAliasOptions(),
            FileName = fileName ?? Secrets.DefaultFileName,
        };

    private static KeyAliasOptions Aliases(params (string Canonical, string Alias)[] pairs)
    {
        KeyAliasOptions options = new();

        foreach ((string canonical, string alias) in pairs)
            options.KeyAliases[canonical] = alias;

        return options;
    }

    [Fact]
    public void TwoSetups_AccumulateAliases_WhenCanonicalKeysArePresent()
    {
        // Both canonical paths exist in the source configuration, which is exactly the case a
        // separate second pass used to overwrite with its raw value.
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
            ["auth_token_ttl"] = "90",
            ["sa:scope:api:auth:token_ttl"] = "30",
        });

        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:token_ttl", "auth_token_ttl"))));

        var config = builder.Build();

        Assert.Equal("https://php.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Equal("90", config["sa:scope:api:auth:token_ttl"]);
    }

    [Fact]
    public void TwoSetups_SecondWithoutAliases_KeepsTheFirstAlias()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
        });

        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        builder.AddSaConfiguration(Options());

        var config = builder.Build();

        Assert.Equal("https://php.example.com", config["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void TwoSetups_SameCanonicalKey_LastMappingWins()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "flat-default",
            ["legacy_base_url"] = "flat-legacy",
        });

        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "legacy_base_url"))));

        var config = builder.Build();

        Assert.Equal("flat-legacy", config["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void TwoSetups_SameCanonicalKeySpelledDifferently_LastMappingWins()
    {
        // `__` is the environment-variable spelling of `:`, so both registrations address the
        // same canonical path and must collapse into one mapping.
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "flat-default",
            ["legacy_base_url"] = "flat-legacy",
        });

        builder.AddSaConfiguration(Options(Aliases(("sa__scope__api__auth__base_url", "auth_base_url"))));
        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "legacy_base_url"))));

        var config = builder.Build();

        Assert.Equal("flat-legacy", config["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void MultipleSetups_RegisterSinglePassAndSingleCommandLineSource()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
        });

        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:token_ttl", "auth_token_ttl"))));
        builder.AddSaConfiguration(Options());

        Assert.Single(builder.Sources.OfType<ISaPostProcessingSource>());
        Assert.Single(builder.Sources.OfType<ArgumentsConfigurationSource>());
    }

    [Fact]
    public void SourceAddedAfterSetups_WinsOverAlias_UntilFinalized()
    {
        // A provider registered after the last call is a normal provider: the configuration root
        // resolves the last provider first, so it beats the alias until the pass is finalized.
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "flat",
        });
        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "late",
        });

        Assert.Equal("late", builder.Build()["sa:scope:api:auth:base_url"]);

        var finalized = new ConfigurationBuilder();
        finalized.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "flat",
        });
        finalized.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        finalized.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "late",
        });
        finalized.FinalizeSaConfiguration();

        Assert.Equal("flat", finalized.Build()["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void Reload_DropsKeyThatDisappearedFromItsSource()
    {
        MutableProvider sourceProvider = new();
        sourceProvider.SetValue("auth_base_url", "flat");

        var builder = new ConfigurationBuilder();
        builder.Add(new MutableSource(sourceProvider));
        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));

        var config = builder.Build();
        Assert.Equal("flat", config["sa:scope:api:auth:base_url"]);

        sourceProvider.RemoveKey("auth_base_url");
        config.Reload();

        Assert.Null(config["sa:scope:api:auth:base_url"]);
        Assert.Null(config["auth_base_url"]);
    }

    [Fact]
    public void WhitespaceValue_BecomesNull()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["blank"] = "   ",
        });
        builder.AddSaConfiguration(Options());

        var config = builder.Build();

        Assert.Null(config["blank"]);
    }

    [Fact]
    public void UnchangedKeys_AreNotCopiedIntoThePass()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["plain"] = "value",
            ["with_secret"] = "{{token}}",
        });

        builder.AddSaPostSecretProcessing(new Secrets(
            new InMemorySecretStore(new Dictionary<string, string?> { ["token"] = "resolved" })));

        var config = builder.Build();
        SaPostProcessingConfigurationProvider provider = ((IConfigurationRoot)config)
            .Providers.OfType<SaPostProcessingConfigurationProvider>().Single();

        Assert.Equal("resolved", config["with_secret"]);
        Assert.Contains("with_secret", provider.StoredKeys);
        Assert.DoesNotContain("plain", provider.StoredKeys);
    }

    [Fact]
    public void TwoSetups_SecretOptions_LastSetupWins()
    {
        using TempDirectory temp = new();
        string secretsFile = temp.Write("secrets.txt", "token=from-last\n");

        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["value"] = "{{token}}",
        });

        builder.AddSaConfiguration(Options(fileName: Path.Combine(temp.Path, "missing.txt")));
        builder.AddSaConfiguration(Options(fileName: secretsFile));

        var config = builder.Build();

        Assert.Equal("from-last", config["value"]);
    }

    [Fact]
    public void TwoSetups_CanonicalValueWins_ComesFromTheLastSetup()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "flat",
            ["sa:scope:api:auth:base_url"] = "canonical",
        });

        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = { ["sa:scope:api:auth:base_url"] = "auth_base_url" },
            CanonicalValueWins = true,
        }));

        var config = builder.Build();

        Assert.Equal("canonical", config["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void CustomTransforms_SeeAliasOutputInRegistrationOrder()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
        });

        builder.AddSaConfiguration(Options(Aliases(("sa:scope:api:auth:base_url", "auth_base_url"))));
        builder.AddSaConfigurationTransform(new SetTransform("feature_flag", "on"));
        builder.AddSaConfigurationTransform(new CopyTransform("sa:scope:api:auth:base_url", "copied_base_url"));

        var config = builder.Build();

        Assert.Equal("on", config["feature_flag"]);
        Assert.Equal("https://php.example.com", config["copied_base_url"]);
    }

    [Fact]
    public void CustomTransformAlone_DoesNotSubstituteSecrets()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["raw"] = "{{missing}}",
        });

        builder.AddSaConfigurationTransform(new SetTransform("other", "value"));

        var config = builder.Build();

        Assert.Equal("value", config["other"]);
        Assert.Equal("{{missing}}", config["raw"]);
    }

    private sealed class SetTransform(string key, string value) : ISaConfigurationTransform
    {
        public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
            => [new(key, value)];
    }

    private sealed class CopyTransform(string from, string to) : ISaConfigurationTransform
    {
        public IEnumerable<KeyValuePair<string, string?>> Apply(ISaConfigurationView configuration)
            => configuration.TryGetValue(from, out string? value) ? [new(to, value)] : [];
    }

    private sealed class MutableProvider : ConfigurationProvider
    {
        public void SetValue(string key, string? value)
        {
            Data[key] = value;
            OnReload();
        }

        public void RemoveKey(string key)
        {
            Data.Remove(key);
            OnReload();
        }
    }

    private sealed class MutableSource(MutableProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }
}
