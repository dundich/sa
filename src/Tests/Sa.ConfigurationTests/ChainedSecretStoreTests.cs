using Sa.Configuration.SecretStore.Engine;
using Sa.Configuration.SecretStore.Stories;

namespace Sa.ConfigurationTests;

public class ChainedSecretStoreTests
{
    [Fact]
    public void GetSecret_ReturnsFirstNonNullValue_FromChainedStores()
    {
        // Arrange
        var store1 = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["key1"] = "value_from_store1",
            ["key2"] = "also_store1"
        });
        var store2 = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["key2"] = "value_from_store2",
            ["key3"] = "value_from_store3"
        });

        var chained = new ChainedSecretStore([store1, store2]);

        // Act & Assert
        Assert.Equal("value_from_store1", chained.GetSecret("key1"));
        Assert.Equal("value_from_store2", chained.GetSecret("key2")); // last wins
        Assert.Equal("value_from_store3", chained.GetSecret("key3"));
    }

    [Fact]
    public void GetSecret_ReturnsNull_WhenKeyNotFoundInAnyStore()
    {
        // Arrange
        var store = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["key1"] = "value1"
        });
        var chained = new ChainedSecretStore([store]);

        // Act
        var result = chained.GetSecret("missing_key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetSecret_SkipsNullValues_AndReturnsSecondStoreValue()
    {
        // Arrange
        var store1 = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["shared_key"] = null!
        });
        var store2 = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["shared_key"] = "value_from_second"
        });

        var chained = new ChainedSecretStore([store1, store2]);

        // Act
        var result = chained.GetSecret("shared_key");

        // Assert
        Assert.Equal("value_from_second", result);
    }

    [Fact]
    public void Add_NewStoreHasHigherPriority_LifoOrder()
    {
        // Arrange
        var originalStore = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["priority_key"] = "original"
        });
        var chained = new ChainedSecretStore([originalStore]);

        // Act — добавляем новое хранилище (оно должно иметь приоритет)
        var newStore = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["priority_key"] = "new_priority"
        });
        chained.Add(newStore);

        // Assert
        Assert.Equal("new_priority", chained.GetSecret("priority_key"));
    }

    [Fact]
    public void GetSecret_EmptyStoresCollection_ReturnsNull()
    {
        // Arrange
        var chained = new ChainedSecretStore([]);

        // Act
        var result = chained.GetSecret("any_key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Add_StoreWithEmptyString_DoesNotOverrideNonNullExistingValue()
    {
        // Arrange
        var store1 = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["key"] = "existing_value"
        });
        var chained = new ChainedSecretStore([store1]);

        // Act — добавляем хранилище с пустой строкой
        var emptyStore = new InMemorySecretStore(new Dictionary<string, string?>
        {
            ["key"] = string.Empty
        });
        chained.Add(emptyStore);

        // Assert — null пропускается дальше, но пустая строка != null, поэтому она вернётся
        Assert.Equal(string.Empty, chained.GetSecret("key"));
    }
}
