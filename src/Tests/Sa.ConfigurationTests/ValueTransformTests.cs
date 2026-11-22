using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sa.Configuration;
using Sa.Configuration.Clients;
using Sa.Configuration.PostProcessing;

namespace Sa.ConfigurationTests;

/// <summary>
/// The two shorthand forms of a transform: per-key value rules and a lambda that returns pairs.
/// </summary>
public class ValueTransformTests
{
    private static Action<SaConfigurationValueTransformBuilder> Rules
        => rules => rules
            .Add("s3_url", value => value?.Trim().ToUpperInvariant())
            .Add("timeout", value => value is null ? null : $"{value}s");

    [Fact]
    public void Rules_RewriteTheirKeys()
    {
        ConfigurationBuilder builder = new();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["s3_url"] = "  b1 ",
            ["timeout"] = "30",
        });

        builder.AddSaConfigurationValueTransform(Rules);

        IConfiguration config = builder.Build();

        Assert.Equal("B1", config["s3_url"]);
        Assert.Equal("30s", config["timeout"]);
    }

    [Fact]
    public void Rules_DoNotInventAbsentKeys_AndDoNotCopyUnchangedOnes()
    {
        ConfigurationBuilder builder = new();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["s3_url"] = " a ",
            ["plain"] = "value",
        });

        builder.AddSaConfigurationValueTransform(rules =>
        {
            Rules(rules);
            rules.Add("missing", _ => "invented");
        });

        IConfiguration config = builder.Build();
        SaPostProcessingConfigurationProvider provider = ((IConfigurationRoot)config)
            .Providers.OfType<SaPostProcessingConfigurationProvider>().Single();

        Assert.Equal("A", config["s3_url"]);
        Assert.Null(config["missing"]);
        Assert.Equal("value", config["plain"]);

        // Only the rule's own key enters the pass; an untouched key keeps reading its source.
        Assert.Contains("s3_url", provider.StoredKeys);
        Assert.DoesNotContain("plain", provider.StoredKeys);
        Assert.DoesNotContain("missing", provider.StoredKeys);
    }

    [Fact]
    public void DelegateTransform_CanExpandOneKeyIntoMany()
    {
        ConfigurationBuilder builder = new();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["key1"] = "s1,s2,s3" });

        builder.AddSaConfigurationTransform(view =>
            view.TryGetValue("key1", out string? raw) && raw is not null
                ? raw.Split(',', StringSplitOptions.TrimEntries)
                    .Select((item, index) => new KeyValuePair<string, string?>($"key1:{index}", item))
                : []);

        IConfiguration config = builder.Build();

        Assert.Equal("s1", config["key1:0"]);
        Assert.Equal("s2", config["key1:1"]);
        Assert.Equal("s3", config["key1:2"]);
    }

    [Fact]
    public void Rules_ApplyToClientOverlays_AndUseTheClientValue()
    {
        ConfigurationManager core = new();
        core.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["s3_url"] = "A",
            ["theme"] = "dark",
        });
        core.AddSaConfigurationValueTransform(Rules);

        using ClientConfigurations clients = Registry(core, new ProviderRows
        {
            Items = [new("acme", "s3_url", " b1 ")],
        });

        Assert.Equal("B1", clients.GetClient("acme")["s3_url"]);
        Assert.Equal("A", core["s3_url"]);

        // A key the client does not own keeps falling through to the core value.
        Assert.Equal("dark", clients.GetClient("acme")["theme"]);
    }

    [Fact]
    public void ClientDoesNotFreezeTheRuleOutputOfACoreOwnedKey()
    {
        MutableSource source = new();
        source.Data["s3_url"] = "a";

        ConfigurationManager core = new();
        ((IConfigurationBuilder)core).Add(source);
        core.AddSaConfigurationValueTransform(Rules);

        using ClientConfigurations clients = Registry(core, new ProviderRows
        {
            Items = [new("acme", "theme", "light")],
        });

        Assert.Equal("A", clients.GetClient("acme")["s3_url"]);

        // The rule's output for a key the client does not own is not stored in the overlay, so the
        // core reload alone is enough — no client reload needed.
        source.Data["s3_url"] = "b";
        ((IConfigurationRoot)core).Reload();

        Assert.Equal("B", core["s3_url"]);
        Assert.Equal("B", clients.GetClient("acme")["s3_url"]);
    }

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

    private static ClientConfigurations Registry(IConfiguration core, ProviderRows rows)
    {
        var registration = new SaClientConfigurationRegistration();
        registration.Add<ProviderRows>((source, _) =>
            ValueTask.FromResult<IReadOnlyList<ClientSetting>>([.. source.Items]));

        return (ClientConfigurations)registration.Build(
            core,
            new ServiceCollection().AddSingleton(rows).BuildServiceProvider());
    }

    private sealed class ProviderRows
    {
        public List<ClientSetting> Items { get; set; } = [];
    }
}
