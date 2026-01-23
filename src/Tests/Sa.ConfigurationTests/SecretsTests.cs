using Sa.Configuration;
using Sa.Configuration.SecretStore;

namespace Sa.ConfigurationTests;

public sealed class SecretsTests(SecretsTests.Fixture fixture) : IClassFixture<SecretsTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        private readonly string SecretsFileName = "secrets.txt";
        private static readonly string EnvSecretsFileName = $"secrets.{SaEnvironment.Default.EnvironmentName}.txt";
        private const string HostKeyFileName = "secrets.host_key.txt";
        private static readonly string HostKeEnvSecretsFileName = $"secrets.host_key.{SaEnvironment.Default.EnvironmentName}.txt";

        public ISecretService Sub => Secrets.Service;

        public ValueTask InitializeAsync()
        {
            // Create a default secrets file for testing
            File.WriteAllText(SecretsFileName, "sa_host_key=host_key\napi_key=my_api_key\n");
            File.WriteAllText(HostKeyFileName, "db_password=my_db_password\n");
            File.WriteAllText(EnvSecretsFileName, "env=true\n");
            File.WriteAllText(HostKeEnvSecretsFileName, "env_host=true\n");

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(SecretsFileName);
            File.Delete(HostKeyFileName);
            File.Delete(EnvSecretsFileName);
            File.Delete(HostKeEnvSecretsFileName);
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
    public void PopulateSecrets_ShouldLoadSecretsFromHostKeyFile()
    {
        // Arrange
        string input = "Database Password: {{db_password}}";

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Equal("Database Password: my_db_password", result);
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
    public void PopulateSecrets_ShouldLoadSecretsFromHostEnvFile()
    {
        // Arrange
        string input = "Env_Host: {{env_host}}";

        // Act
        var result = Sub.PopulateSecrets(input);

        // Assert
        Assert.Equal("Env_Host: true", result);
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
        Assert.Equal("The secret name 'missing_key' was not present in vault. Ensure that you have a local `secrets.txt` file in the src folder.", exception.Message);
    }
}
