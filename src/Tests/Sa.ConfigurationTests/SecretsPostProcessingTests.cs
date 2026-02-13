using Microsoft.Extensions.Configuration;
using Sa.Configuration.SecretStore;
using Sa.Configuration.SecretStore.Stories;

namespace Sa.ConfigurationTests;

public class SecretsPostProcessingTests
{
    [Fact]
    public void ExtractValueFromDoubleBraces_SimpleValue()
    {
        // Arrange
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SimpleKey"] = "{{simple_value}}",
                ["EmptyKey"] = "{{empty_value}}",
                ["NullKey"] = "{{null_value}}",
                ["CombineWithNullKey"] = "{{simple_value}} {{null_value}}",
                ["CombineKey"] = "{{simple_value}} + {{simple_value}}",
                ["CombineWithEmptyKey"] = "{{empty_value}} + {{simple_value}}",
            });

        var store = new Secrets(
            new InMemorySecretStore(
                new Dictionary<string, string?>
                {
                    ["simple_value"] = "SimpleValue",
                    ["empty_value"] = String.Empty
                }));

        builder.AddSaPostSecretProcessing(store);

        var config = builder.Build();

        // Assert
        Assert.Equal("SimpleValue", config["SimpleKey"]);

        Assert.Null(config["EmptyKey"]);

        Assert.Null(config["NullKey"]);

        Assert.Null(config["CombineWithNullKey"]);

        Assert.Equal("SimpleValue + SimpleValue", config["CombineKey"]);
        Assert.Equal(" + SimpleValue", config["CombineWithEmptyKey"]);
    }

    [Fact]
    public void ExtractValueFromDoubleBraces_NestedSections()
    {
        // Arrange
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Section:SubSection:Key"] = "{{nested_value}}",
                ["Section:AnotherKey"] = "{{another}}"
            });

        var store = new Secrets(
            new InMemorySecretStore(
                new Dictionary<string, string?>
                {
                    ["nested_value"] = "NestedValue",
                    ["another"] = "Another"
                }));

        builder.AddSaPostSecretProcessing(store);

        var config = builder.Build();

        // Assert
        Assert.Equal("NestedValue", config["Section:SubSection:Key"]);
        Assert.Equal("Another", config["Section:AnotherKey"]);

        var section = config.GetSection("Section:SubSection");
        Assert.Equal("NestedValue", section["Key"]);
    }

    [Fact]
    public void PreserveNormalValues_WithoutBraces()
    {
        // Arrange
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NormalKey"] = "normal_value",
                ["Number"] = "42",
                ["Empty"] = ""
            });

        var store = new Secrets(
            new InMemorySecretStore(
                new Dictionary<string, string?>
                {
                    ["NormalKey"] = "SimpleValue"
                }));

        builder.AddSaPostSecretProcessing(store);

        var config = builder.Build();

        // Assert
        Assert.Equal("normal_value", config["NormalKey"]);
        Assert.Equal("42", config["Number"]);
        Assert.Null(config["Empty"]);
    }
}
