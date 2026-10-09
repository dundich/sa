using System.ComponentModel.DataAnnotations;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// The options are a single mutable type served by the standard options pipeline. There is no second
/// settings type and no hand-written mapping any more, so a property cannot be dropped between
/// configuration and the provider — which is exactly how <c>BufferSize</c> used to go missing. The
/// storage root and the file-I/O policy now live on <c>TempFolderOptions</c> behind the mandatory
/// <c>TempFolder</c> channel, so this type only keeps the file-ID / basket view.
/// </summary>
public sealed class FileSystemStorageOptionsTests
{
    private static FileSystemStorageOptions ValidOptions() => new();

    [Fact]
    public void Defaults_AreTheSharedNamingDefaults()
    {
        var options = new FileSystemStorageOptions();

        Assert.Equal("fs", options.StorageType);
        Assert.Equal(Sa.HybridFileStorage.StorageNaming.DefaultBasket, options.Basket);
        Assert.False(options.IsReadOnly);
    }

    [Fact]
    public void DefaultBasket_IsSharedWithTheBasePackage()
    {
        Assert.Equal(
            Sa.HybridFileStorage.StorageNaming.DefaultBasket,
            FileSystemStorageOptions.DefaultBasket);
    }

    [Fact]
    public void DefaultStorageType_IsExposedAsAConstant()
    {
        Assert.Equal("fs", FileSystemStorageOptions.DefaultStorageType);
    }

    [Fact]
    public void Validate_Accepts_Defaults()
    {
        ValidOptions().Validate();
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
    public void Validate_Rejects_TooLongBasket()
    {
        var options = ValidOptions();
        options.Basket = new string('a', 64);

        Assert.Throws<ValidationException>(() => options.Validate());
    }
}
