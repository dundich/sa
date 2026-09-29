using Microsoft.Extensions.Logging;
using Sa.Configuration.SecretStore.Stories;

namespace Sa.ConfigurationTests;

public class FileSecretStoreTests
{
    [Fact]
    public void Ctor_MissingFile_ReturnsNull_ForAnyKey()
    {
        // Arrange
        var store = new FileSecretStore("definitely_missing_secrets_file_xyz.txt");

        // Act & Assert
        Assert.Null(store.GetSecret("any_key"));
    }

    [Fact]
    public void Ctor_SkipsLinesWithoutSeparator()
    {
        string path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, "good_key=good_value\nmalformed line without separator\nanother=one\n");

            // Act
            var store = new FileSecretStore(path);

            // Assert
            Assert.Equal("good_value", store.GetSecret("good_key"));
            Assert.Equal("one", store.GetSecret("another"));
            Assert.Null(store.GetSecret("malformed line without separator"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ctor_SkipsCommentsAndEmptyLines()
    {
        string path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, "# comment line\n\n   \nkey=value\n   # indented comment\n");

            // Act
            var store = new FileSecretStore(path);

            // Assert
            Assert.Equal("value", store.GetSecret("key"));
            Assert.Null(store.GetSecret("# comment line"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ctor_TakesLastValue_ForDuplicateKeys()
    {
        string path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, "key=first\nkey=second\n");

            // Act
            var store = new FileSecretStore(path);

            // Assert
            Assert.Equal("second", store.GetSecret("key"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
