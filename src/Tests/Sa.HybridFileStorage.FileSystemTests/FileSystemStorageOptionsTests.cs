using System.ComponentModel.DataAnnotations;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// The mutable options and the immutable settings must never disagree: the provider registration
/// used to copy properties by hand, which is how <c>BufferSize</c> went missing.
/// </summary>
public sealed class FileSystemStorageOptionsTests
{
    private static FileSystemStorageOptions ValidOptions() => new()
    {
        BasePath = Path.Combine(Path.GetTempPath(), "fs_options_test"),
    };

    [Fact]
    public void ToSettings_CopiesEveryProperty()
    {
        var options = new FileSystemStorageOptions
        {
            BasePath = "/tmp/data",
            StorageType = "fsx",
            Basket = "documents",
            IsReadOnly = true,
            BufferSize = 4096,
        };

        var settings = options.ToSettings();

        Assert.Equal("/tmp/data", settings.BasePath);
        Assert.Equal("fsx", settings.StorageType);
        Assert.Equal("documents", settings.Basket);
        Assert.True(settings.IsReadOnly);
        Assert.Equal(4096, settings.BufferSize);
    }

    [Fact]
    public void ToSettings_DefaultsMatchTheSettingsDefaults()
    {
        var settings = new FileSystemStorageOptions { BasePath = "/tmp/data" }.ToSettings();

        Assert.Equal(FileSystemStorageSettings.DefaultStorageType, settings.StorageType);
        Assert.Equal(FileSystemStorageSettings.DefaultBasket, settings.Basket);
        Assert.False(settings.IsReadOnly);
        Assert.Equal(256 * 1024, settings.BufferSize);
    }

    [Fact]
    public void DefaultBasket_IsSharedWithTheBasePackage()
    {
        Assert.Equal(Sa.HybridFileStorage.StorageNaming.DefaultBasket, FileSystemStorageSettings.DefaultBasket);
    }

    [Fact]
    public void Validate_Accepts_Defaults()
    {
        ValidOptions().Validate();
    }

    [Fact]
    public void Validate_Rejects_EmptyBasePath()
    {
        var ex = Assert.Throws<ValidationException>(() => new FileSystemStorageOptions().Validate());
        Assert.Contains("BasePath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_Rejects_NonPositiveBufferSize(int bufferSize)
    {
        var options = ValidOptions();
        options.BufferSize = bufferSize;

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("BufferSize", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("1basket")]
    [InlineData("a/b")]
    public void Validate_Rejects_MalformedBasket(string basket)
    {
        var options = ValidOptions();
        options.Basket = basket;

        Assert.Throws<ValidationException>(() => options.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Rejects_EmptyStorageType(string storageType)
    {
        var options = ValidOptions();
        options.StorageType = storageType;

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("StorageType", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_TooLongStorageType()
    {
        var options = ValidOptions();
        options.StorageType = new string('a', 11);

        Assert.Throws<ValidationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_DoesNotNestTheBasePathWrapper()
    {
        // The old code caught its own `ValidationException` in a bare `catch (Exception ex)` and
        // re-wrapped it, so a nested message read
        // "Invalid BasePath format: x. Invalid BasePath format: x. ...". Whether the inner
        // ValidationException is reachable depends on the platform's invalid-char set, so assert
        // the invariant rather than a specific branch: the prefix appears at most once.
        var options = ValidOptions();
        options.BasePath = "a\0b";

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Equal(1, CountOccurrences(ex.Message, "Invalid BasePath format"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
