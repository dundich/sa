using Sa.Configuration.SecretStore.Stories;

namespace Sa.ConfigurationTests;

public class CommandLineArgsSecretStoreTests
{
    [Fact]
    public void GetSecret_ReturnsValue_WhenKeyExists()
    {
        // Arrange
        var args = new[] { "--db_password", "my_secret_pass" };
        var store = new CommandLineArgsSecretStore(args);

        // Act
        var result = store.GetSecret("db_password");

        // Assert
        Assert.Equal("my_secret_pass", result);
    }

    [Fact]
    public void GetSecret_ReturnsNull_WhenKeyDoesNotExist()
    {
        // Arrange
        var args = new[] { "--existing_key", "value" };
        var store = new CommandLineArgsSecretStore(args);

        // Act
        var result = store.GetSecret("non_existing_key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetSecret_ReturnsTrue_WhenFlagPresentWithoutValue()
    {
        // Arrange
        var args = new[] { "--verbose_flag" };
        var store = new CommandLineArgsSecretStore(args);

        // Act
        var result = store.GetSecret("verbose_flag");

        // Assert
        Assert.Equal("true", result);
    }

    [Fact]
    public void GetSecret_ReturnsNull_ForDefaultArgs_WhenNoArgsProvided()
    {
        // Arrange
        var store = new CommandLineArgsSecretStore();

        // Act
        var result = store.GetSecret("any_key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetSecret_SupportsDoubleDashPrefix()
    {
        // Arrange
        var args = new[] { "--api-key", "abc-123" };
        var store = new CommandLineArgsSecretStore(args);

        // Act
        var result = store.GetSecret("api-key");

        // Assert
        Assert.Equal("abc-123", result);
    }
}
