using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

public class SecretServiceTests
{
    private ISecretService Sub { get; }

    public SecretServiceTests()
    {
        // Set up an in-memory secret store with some test secrets
        var secrets = new Dictionary<string, string?>
        {
            { "db_password", "my_secret_password" },
            { "api_key", "my_api_key" },
            { "key_with_spaces", "   secret_value   " }
        };

        var secretStore = new InMemorySecretStore(secrets);
        Sub = new Secrets(secretStore);
    }

    [Fact]
    public void PopulateSecrets_ShouldReplacePlaceholders_WithSecretValues()
    {
        // Arrange
        string input = "Database password: {{db_password}}, API key: {{api_key}}";

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Equal("Database password: my_secret_password, API key: my_api_key", result);
    }

    [Fact]
    public void PopulateSecrets_ShouldThrow_WhenSecretNotFound()
    {
        // Arrange
        string input = "Database password: {{db_password}}, Missing secret: {{missing_key}}";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => Sub.PopulateSecrets(input));
        Assert.Equal("The secret name 'missing_key' was not present in vault. Ensure that you have a local `secrets.production.txt` file in the src folder.", exception.Message);
    }

    [Fact]
    public void PopulateSecrets_ShouldReturnNull_WhenInputIsNullOrWhitespace()
    {
        // Arrange
        string? input = null;

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void PopulateSecrets_ShouldHandleMultiplePlaceholders()
    {
        // Arrange
        string input = "Key1: {{db_password}}, Key2: {{api_key}}";

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Equal("Key1: my_secret_password, Key2: my_api_key", result);
    }

    [Fact]
    public void PopulateSecrets_ShouldTrimSecretValues()
    {
        // Arrange
        string input = "Secret: {{key_with_spaces}}";

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Equal("Secret: secret_value", result);
    }

    [Fact]
    public void AddStore_AddsNewStore()
    {
        // Arrange
        var secrets = new Secrets(new InMemorySecretStore().AddSecret("AnotherKey", "AnotherValue"));

        // Assert
        var result = secrets.GetSecret("AnotherKey");
        Assert.Equal("AnotherValue", result);

        // Act 2
        var secretStore2 = new InMemorySecretStore();
        secretStore2.AddSecret("AnotherKey", "AnotherValue 2");
        secrets.AddStore(secretStore2);

        // Assert 2
        var result2 = secrets.GetSecret("AnotherKey");
        Assert.Equal("AnotherValue 2", result2);

        // Act 3
        secrets.AddStore(new InMemorySecretStore().AddSecret("AnotherKey", default));

        // Assert 3
        var result3 = secrets.GetSecret("AnotherKey");
        Assert.Equal("AnotherValue 2", result3);

        // Act 4
        secrets.AddStore(new InMemorySecretStore().AddSecret("AnotherKey", string.Empty));

        // Assert 4
        var result4 = secrets.GetSecret("AnotherKey");
        Assert.Empty(result4!);
    }
}
