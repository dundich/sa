using Microsoft.Extensions.Configuration;
using Sa.Configuration;
using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

/// <summary>
/// The explicit key-alias feature lives at the final post-secrets stage: it runs over the merged
/// configuration of every <see cref="IConfiguration"/> provider, so the alias can fill a
/// canonical path no single source holds.
/// </summary>
public class KeyAliasesTests
{
    // The feature is exercised through AddSaConfiguration's post-secrets stage, with the file
    // layers switched off so the tests are hermetic.
    private static SecretOptions Options(KeyAliasOptions keyAliases)
        => new() { LoadDotEnv = false, Encryption = new SecretEncryptionOptions { Enabled = false }, KeyAliases = keyAliases };

    [Fact]
    public void AliasValue_FillsCanonicalPath_WhenCanonicalPathAbsent()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = "auth_base_url",
            },
        }));

        var config = builder.Build();

        // The flat key fills the canonical C# path, and the flat key stays readable directly.
        Assert.Equal("https://php.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://php.example.com", config["auth_base_url"]);
    }

    [Fact]
    public void AliasValue_WinsByDefault_WhenCanonicalPathAlsoPresent()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
            ["auth_base_url"] = "https://php.example.com",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = "auth_base_url",
            },
        }));

        var config = builder.Build();

        // Default: the non-blank alias value always wins for the canonical path.
        Assert.Equal("https://php.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://php.example.com", config["auth_base_url"]);
    }

    [Fact]
    public void CanonicalValue_Wins_WhenCanonicalValueWinsIsSet()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
            ["auth_base_url"] = "https://php.example.com",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = "auth_base_url",
            },
            CanonicalValueWins = true,
        }));

        var config = builder.Build();

        // A non-blank canonical value any provider already filled wins over the alias.
        Assert.Equal("https://csharp.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://php.example.com", config["auth_base_url"]);
    }

    [Fact]
    public void AliasValue_FillsBlankCanonicalValue()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "",
            ["auth_base_url"] = "https://php.example.com",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = "auth_base_url",
            },
            CanonicalValueWins = true,
        }));

        var config = builder.Build();

        // A blank canonical value does not protect the path — the alias fills it.
        Assert.Equal("https://php.example.com", config["sa:scope:api:auth:base_url"]);
    }

    [Fact]
    public void MissingAliasValue_NeverTouchesCanonicalPath()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = "auth_base_url",
            },
        }));

        var config = builder.Build();

        // auth_base_url is absent: the canonical value is left as loaded, and no flat key appears.
        Assert.Equal("https://csharp.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Null(config["auth_base_url"]);
    }

    [Fact]
    public void BlankAliasValue_NeverTouchesCanonicalPath()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
            ["auth_base_url"] = "   ",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = "auth_base_url",
            },
            CanonicalValueWins = true,
        }));

        var config = builder.Build();

        // A blank alias value is always "no value" — even with CanonicalValueWins the canonical
        // value survives, and it is never fabricated from a blank row. The blank alias key itself
        // is normalized to null by the secrets pass, so it reads as null.
        Assert.Equal("https://csharp.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Null(config["auth_base_url"]);
    }

    [Fact]
    public void Alias_FillsCanonicalPath_NoSingleSourceHoldsIt()
    {
        // The flat key comes from one provider, the canonical path from another. The alias runs
        // over the merged configuration, so it can fill a canonical path no single source holds.
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
        });
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = "auth_base_url",
            },
        }));

        var config = builder.Build();

        Assert.Equal("https://php.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://php.example.com", config["auth_base_url"]);
    }

    [Fact]
    public void MappingKeys_AreCaseInsensitiveAndSeparatorNormalized()
    {
        // The mapping entries themselves are compared the same way configuration keys are:
        // case-insensitively, and spelled with `__` for the `:` separator. The data keys here are
        // stored in canonical `:` form (as the PG / environment providers store them), so this
        // isolates how the *mapping* entry is matched, not how a data provider normalizes its own keys.
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The alias row is stored with a different case than the mapping.
            ["Auth_Base_Url"] = "https://php.example.com",
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
        });
        builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                // The canonical side is spelled with `__` for its `:` separators (the `base_url` segment
        // keeps its single data underscore); the alias side is the flat PHP key.
                ["sa__scope__api__auth__base_url"] = "auth_base_url",
            },
        }));

        var config = builder.Build();

        // The `__`-spelled mapping resolves to the same keys as a `:`-spelled one, and the
        // case-differing alias row is still found. The alias value wins for the canonical path.
        Assert.Equal("https://php.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://php.example.com", config["Auth_Base_Url"]);
    }

    [Fact]
    public void AddSaPostSecretProcessing_Throws_OnBlankCanonicalKey()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
        });

        Assert.Throws<ArgumentException>(() => builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["   "] = "auth_base_url",
            },
        })));
    }

    [Fact]
    public void AddSaPostSecretProcessing_Throws_OnBlankAliasKey()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["auth_base_url"] = "https://php.example.com",
        });

        Assert.Throws<ArgumentException>(() => builder.AddSaConfiguration(Options(new KeyAliasOptions
        {
            KeyAliases = new Dictionary<string, string>
            {
                ["sa:scope:api:auth:base_url"] = " ",
            },
        })));
    }

    [Fact]
    public void EmptyAliasMap_LeavesPipelineUnchanged()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["sa:scope:api:auth:base_url"] = "https://csharp.example.com",
            ["auth_base_url"] = "https://php.example.com",
        });
        builder.AddSaConfiguration();

        var config = builder.Build();

        // No mappings declared: the pipeline is exactly as it was before the feature.
        Assert.Equal("https://csharp.example.com", config["sa:scope:api:auth:base_url"]);
        Assert.Equal("https://php.example.com", config["auth_base_url"]);
    }
}
