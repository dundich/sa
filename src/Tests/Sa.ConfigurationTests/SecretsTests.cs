using Microsoft.Extensions.Configuration;
using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

public sealed class SecretsTests(SecretsTests.Fixture fixture) : IClassFixture<SecretsTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public const string SecretsFileName = "secrets.txt";
        private const string EnvironmentName = "test";
        private static readonly string EnvSecretsFileName = $"secrets.{EnvironmentName}.txt";

        public ISecretService Sub => Secrets.CreateDefault(new SecretOptions
        {
            EnvironmentName = EnvironmentName,
            FileName = SecretsFileName,
        });

        public ValueTask InitializeAsync()
        {
            // Create a default secrets file for testing
            File.WriteAllText(SecretsFileName, "sa_host_key=host_key\napi_key=my_api_key\n");
            File.WriteAllText(EnvSecretsFileName, "env=true\n");

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(SecretsFileName);
            File.Delete(EnvSecretsFileName);
            return ValueTask.CompletedTask;
        }
    }

    private ISecretService Sub => fixture.Sub;


    [Fact]
    public void PopulateSecrets_ShouldLoadSecretsFromFile()
    {
        // Arrange
        string input = "API Key: {{api_key}}, Host Key: {{sa_host_key}}";

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Equal("API Key: my_api_key, Host Key: host_key", result);
    }


    [Fact]
    public void PopulateSecrets_ShouldLoadSecretsFromEnvFile()
    {
        // Arrange
        string input = "Env: {{env}}";

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Equal("Env: true", result);
    }


    [Fact]
    public void PopulateSecrets_ShouldReturnNull_WhenInputIsNullOrWhitespace()
    {
        // Act
        var result = Sub.PopulateSecrets(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void PopulateSecrets_ShouldThrow_WhenSecretNotFound()
    {
        // Arrange
        string input = "Missing Secret: {{missing_key}}";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => Sub.PopulateSecrets(input));
        Assert.StartsWith("The secret", exception.Message);
    }

    [Fact]
    public void CheckConfigurationWithSecretFiles()
    {
        // Arrange
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SimpleKey"] = "API Key: {{api_key}}, Host Key: {{sa_host_key}}",
            });

        builder.AddSaPostSecretProcessing(Sub);

        var config = builder.Build();

        Assert.Equal("API Key: my_api_key, Host Key: host_key", config["SimpleKey"]);
    }
}
