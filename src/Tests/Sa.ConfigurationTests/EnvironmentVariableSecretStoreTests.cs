using Sa.Configuration.SecretStore.Stories;

namespace Sa.ConfigurationTests;

public class EnvironmentVariableSecretStoreTests
{
    [Fact]
    public void GetSecret_ReturnsValue_WhenEnvironmentVariableExists()
    {
        // Arrange
        const string key = "__TEST_ENV_VAR_EXISTS__";
        const string expectedValue = "test_secret_value";
        Environment.SetEnvironmentVariable(key, expectedValue);

        try
        {
            var store = new EnvironmentVariableSecretStore();

            // Act
            var result = store.GetSecret(key);

            // Assert
            Assert.Equal(expectedValue, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void GetSecret_ReturnsEmptyString_WhenValueEqualsParenthesizedEmpty()
    {
        // Arrange
        const string key = "__TEST_ENV_VAR_EMPTY__";
        Environment.SetEnvironmentVariable(key, "(empty)");

        try
        {
            var store = new EnvironmentVariableSecretStore();

            // Act
            var result = store.GetSecret(key);

            // Assert
            Assert.Equal(string.Empty, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void GetSecret_ReturnsNull_WhenEnvironmentVariableDoesNotExist()
    {
        // Arrange
        const string key = "__NON_EXISTENT_TEST_ENV_VAR__";
        var store = new EnvironmentVariableSecretStore();

        // Act
        var result = store.GetSecret(key);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetSecret_ReturnsNormalValue_WhenNotParenthesizedEmpty()
    {
        // Arrange
        const string key = "__TEST_ENV_VAR_NORMAL__";
        const string expectedValue = "(not_empty)";
        Environment.SetEnvironmentVariable(key, expectedValue);

        try
        {
            var store = new EnvironmentVariableSecretStore();

            // Act
            var result = store.GetSecret(key);

            // Assert
            Assert.Equal(expectedValue, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
