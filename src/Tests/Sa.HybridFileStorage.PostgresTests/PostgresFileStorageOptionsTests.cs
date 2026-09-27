using Sa.HybridFileStorage.Postgres;

namespace Sa.HybridFileStorage.PostgresTests;

/// <summary>
/// These options used to reach the DDL completely unvalidated. A malformed <c>TableName</c> or
/// <c>StorageType</c> only surfaced as <c>relation does not exist</c> on the first upload, and a
/// <c>StorageType</c> needing sanitisation produced file IDs the storage could not parse back.
/// </summary>
public sealed class PostgresFileStorageOptionsTests
{
    private static PostgresFileStorageOptions ValidOptions() => new();

    [Fact]
    public void Validate_Accepts_Defaults()
    {
        ValidOptions().Validate();
    }

    // ---------- TableName ----------

    [Theory]
    [InlineData("files")]
    [InlineData("binary_data")]
    [InlineData("_files")]
    [InlineData("Files2025")]
    public void Validate_Accepts_GoodTableName(string tableName)
    {
        var options = ValidOptions();
        options.TableName = tableName;

        options.Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Rejects_EmptyTableName(string? tableName)
    {
        var options = ValidOptions();
        options.TableName = tableName!;

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.TableName), ex.ParamName);
    }

    [Theory]
    [InlineData("\"files\"")]     // was silently Trim('"')-ed, masking the typo
    [InlineData("my files")]
    [InlineData("files;DROP TABLE x")]
    [InlineData("1files")]
    public void Validate_Rejects_MalformedTableName(string tableName)
    {
        var options = ValidOptions();
        options.TableName = tableName;

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.TableName), ex.ParamName);
    }

    [Fact]
    public void Validate_Rejects_TooLongTableName()
    {
        var options = ValidOptions();
        options.TableName = new string('a', 64);

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    // ---------- StorageType ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pg:1")]         // a colon is found before the "://" scheme separator
    [InlineData("pg/1")]
    public void Validate_Rejects_StorageTypeThatWouldBreakFileIds(string? storageType)
    {
        var options = ValidOptions();
        options.StorageType = storageType!;

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.StorageType), ex.ParamName);
    }

    [Fact]
    public void Validate_Rejects_TooLongStorageType()
    {
        var options = ValidOptions();
        options.StorageType = new string('a', 11);

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    // ---------- Basket ----------

    [Theory]
    [InlineData("ab")]
    [InlineData("1share")]
    [InlineData("a/b")]
    public void Validate_Rejects_MalformedBasket(string basket)
    {
        var options = ValidOptions();
        options.Basket = basket;

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.Basket), ex.ParamName);
    }

    // ---------- SchemaName ----------

    [Fact]
    public void Validate_Allows_NullSchemaName_SoItCanBeAutoDetected()
    {
        var options = ValidOptions();
        options.SchemaName = null;

        options.Validate();
    }

    [Fact]
    public void Validate_Rejects_SchemaNameList()
    {
        // The auto-detection splits search_path on ',' and takes element 0, so an explicit
        // comma-separated value would register the table where nothing can query it.
        var options = ValidOptions();
        options.SchemaName = "storage,public";

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.SchemaName), ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1storage")]
    [InlineData("storage; DROP SCHEMA x")]
    public void Validate_Rejects_MalformedSchemaName(string schemaName)
    {
        var options = ValidOptions();
        options.SchemaName = schemaName;

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    // ---------- numeric options ----------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Rejects_NonPositiveExpireDays(int days)
    {
        var options = ValidOptions();
        options.ExpireDays = days;

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.ExpireDays), ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Rejects_NonPositiveMigrationScheduleForwardDays(int days)
    {
        var options = ValidOptions();
        options.MigrationScheduleForwardDays = days;

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.MigrationScheduleForwardDays), ex.ParamName);
    }

    [Fact]
    public void Validate_ReportsTheFirstOffendingProperty()
    {
        var options = new PostgresFileStorageOptions
        {
            TableName = "\"files\"",
            StorageType = string.Empty,
            Basket = "ab",
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(options.TableName), ex.ParamName);
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var options = new PostgresFileStorageOptions();

        Assert.Null(options.SchemaName);
        Assert.Equal("files", options.TableName);
        Assert.Equal("pg", options.StorageType);
        Assert.Equal(Sa.HybridFileStorage.StorageNaming.DefaultBasket, options.Basket);
        Assert.False(options.IsReadOnly);
        Assert.Equal(365 * 3, options.ExpireDays);
        Assert.Equal(2, options.MigrationScheduleForwardDays);
        Assert.Equal("Day", options.PgPartBy.ToString());
    }
}
