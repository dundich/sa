using Sa.HybridFileStorage;

namespace Sa.HybridFileStorageTests;

/// <summary>
/// Boundary tests for the shared naming rules. Every provider routes its <c>StorageType</c> and
/// <c>Basket</c> through these, so a rule that is too loose here shows up as an unusable file ID
/// in a provider-specific test far away from the cause.
/// </summary>
public sealed class StorageNamingTests
{
    // ---------- ValidateBasket ----------

    [Theory]
    [InlineData("share")]
    [InlineData("documents")]
    [InlineData("_private")]
    [InlineData("basket-a")]      // dashes are legal: the basket is used verbatim as a directory name
    [InlineData("basket_a")]
    [InlineData("abc")]           // lower bound
    public void ValidateBasket_Accepts(string basket)
    {
        StorageNaming.ValidateBasket(basket, "basket");
    }

    [Fact]
    public void ValidateBasket_Accepts_MaxLength()
    {
        StorageNaming.ValidateBasket(new string('a', StorageNaming.BasketMaxLength), "basket");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateBasket_Rejects_Empty(string? basket)
    {
        var ex = Assert.Throws<ArgumentException>(() => StorageNaming.ValidateBasket(basket, "basket"));
        Assert.Equal("basket", ex.ParamName);
    }

    [Theory]
    [InlineData("ab")]            // below the lower bound
    [InlineData("1basket")]       // must start with a letter or underscore
    [InlineData("-basket")]
    public void ValidateBasket_Rejects_Malformed(string basket)
    {
        Assert.Throws<ArgumentException>(() => StorageNaming.ValidateBasket(basket, "basket"));
    }

    [Fact]
    public void ValidateBasket_Rejects_MaxLengthPlusOne()
    {
        var tooLong = new string('a', StorageNaming.BasketMaxLength + 1);
        Assert.Throws<ArgumentException>(() => StorageNaming.ValidateBasket(tooLong, "basket"));
    }

    [Theory]
    [InlineData("a/b")]           // would add a path segment to the file ID
    [InlineData("a\\b")]
    public void ValidateBasket_Rejects_PathSeparator(string basket)
    {
        var ex = Assert.Throws<ArgumentException>(() => StorageNaming.ValidateBasket(basket, "basket"));
        Assert.Contains("path separator", ex.Message, StringComparison.Ordinal);
    }

    // ---------- RequireStorageType ----------

    [Theory]
    [InlineData("fs")]
    [InlineData("pg")]
    [InlineData("s3")]
    [InlineData("azure-blob")]    // dashes are legal in a URI scheme
    public void RequireStorageType_ReturnsValue(string storageType)
    {
        Assert.Equal(storageType, StorageNaming.RequireStorageType(storageType, "storageType"));
    }

    [Fact]
    public void RequireStorageType_Accepts_MaxLength()
    {
        var value = new string('a', StorageNaming.StorageTypeMaxLength);
        Assert.Equal(value, StorageNaming.RequireStorageType(value, "storageType"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RequireStorageType_Rejects_Empty(string? storageType)
    {
        Assert.Throws<ArgumentException>(() => StorageNaming.RequireStorageType(storageType, "storageType"));
    }

    [Theory]
    [InlineData("fs://")]         // ':' would be found before the scheme separator "://"
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void RequireStorageType_Rejects_SchemeBreakingChars(string storageType)
    {
        var ex = Assert.Throws<ArgumentException>(() => StorageNaming.RequireStorageType(storageType, "storageType"));
        Assert.Contains("scheme of a file ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireStorageType_Rejects_TooLong()
    {
        var tooLong = new string('a', StorageNaming.StorageTypeMaxLength + 1);
        Assert.Throws<ArgumentException>(() => StorageNaming.RequireStorageType(tooLong, "storageType"));
    }

    // ---------- RequireIdentifier ----------

    [Theory]
    [InlineData("files")]
    [InlineData("_files")]
    [InlineData("binary_data")]
    [InlineData("Files2025")]
    public void RequireIdentifier_ReturnsValue(string identifier)
    {
        Assert.Equal(identifier, StorageNaming.RequireIdentifier(identifier, "identifier"));
    }

    [Fact]
    public void RequireIdentifier_Accepts_MaxLength()
    {
        var value = new string('a', StorageNaming.IdentifierMaxLength);
        Assert.Equal(value, StorageNaming.RequireIdentifier(value, "identifier"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireIdentifier_Rejects_Empty(string? identifier)
    {
        Assert.Throws<ArgumentException>(() => StorageNaming.RequireIdentifier(identifier, "identifier"));
    }

    [Theory]
    [InlineData("\"files\"")]      // quoting used to be silently trimmed, which is how the DDL and the
    [InlineData("my files")]      // queries ended up disagreeing on the table name
    [InlineData("files; DROP TABLE x")]
    [InlineData("files-1")]
    public void RequireIdentifier_Rejects_Malformed(string identifier)
    {
        Assert.Throws<ArgumentException>(() => StorageNaming.RequireIdentifier(identifier, "identifier"));
    }

    [Theory]
    [InlineData("1files")]
    [InlineData("-files")]
    public void RequireIdentifier_Rejects_BadFirstChar(string identifier)
    {
        var ex = Assert.Throws<ArgumentException>(() => StorageNaming.RequireIdentifier(identifier, "identifier"));
        Assert.Contains("start with a letter or an underscore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireIdentifier_Rejects_TooLong()
    {
        var tooLong = new string('a', StorageNaming.IdentifierMaxLength + 1);
        Assert.Throws<ArgumentException>(() => StorageNaming.RequireIdentifier(tooLong, "identifier"));
    }
}
