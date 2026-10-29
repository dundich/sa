using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests (no database) for the identifier handling of the Outbox DDL: names go through
/// <c>EscapeIdentifier</c>, which must double a quote the PostgreSQL way, and the two constraint names
/// the builder derives itself must be escaped as well.
/// </summary>
public class IdentifierEscapingTests
{
    [Fact]
    public void EscapeIdentifier_DoubleQuoteInName_IsDoubled()
    {
        // Inside a quoted identifier a quote is escaped by doubling IT. Doubling it as two
        // apostrophes - what this used to do - produced a valid but different name, so every object
        // was created under a name the caller never configured.
        Assert.Equal("\"we\"\"ird\"", "we\"ird".EscapeIdentifier());
    }

    [Fact]
    public void EscapeIdentifier_SingleQuoteInName_IsNotAnEscape()
    {
        // An apostrophe is ordinary text inside a double-quoted identifier.
        Assert.Equal("\"we'ird\"", "we'ird".EscapeIdentifier());
    }

    [Fact]
    public void EscapeIdentifier_NameOver63Bytes_IsRejected()
    {
        // PostgreSQL truncates on UTF-8 bytes, so a 40-character Cyrillic name is over the limit.
        string tooLong = new('ж', 40);

        Assert.Throws<ArgumentException>(() => tooLong.EscapeIdentifier());
    }

    [Fact]
    public void EscapeIdentifier_NameOfExactly63Bytes_IsAccepted()
    {
        string exact = new('a', 63);

        Assert.Equal($"\"{exact}\"", exact.EscapeIdentifier());
    }

    [Fact]
    public void EscapeIdentifier_LineBreakOrBackslash_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => "a\nb".EscapeIdentifier());
        Assert.Throws<ArgumentException>(() => @"a\b".EscapeIdentifier());
    }

    [Fact]
    public void TypeTable_EscapesTheDerivedConstraintName()
    {
        var settings = new PgOutboxTableSettings
        {
            Type = { TableName = "ty\"pe" },
        };

        var sql = new SqlOutboxBuilder(settings);

        Assert.Contains("\"public\".\"ty\"\"pe\"", sql.SqlCreateTypeTable);
        Assert.Contains("CONSTRAINT \"pk_ty\"\"pe\" PRIMARY KEY", sql.SqlCreateTypeTable);
    }

    [Fact]
    public void OffsetTable_EscapesTheDerivedConstraintName()
    {
        var settings = new PgOutboxTableSettings
        {
            Offset = { TableName = "of\"fset" },
        };

        var sql = new SqlOutboxBuilder(settings);

        Assert.Contains("\"public\".\"of\"\"fset\"", sql.SqlCreateOffsetTable);
        Assert.Contains("CONSTRAINT \"pk_of\"\"fset\" PRIMARY KEY", sql.SqlCreateOffsetTable);
    }

    [Fact]
    public void Validate_RejectsATableNameThatWouldEndTheStatement()
    {
        // The table names are the identifiers this library splices into DDL; a statement terminator
        // in one of them is the one mistake that turns a typo into a second statement.
        var settings = new PgOutboxTableSettings();
        settings.Message.TableName = "outbox; DROP TABLE users";

        Assert.Throws<ArgumentException>(() => settings.Validate());
    }

    [Fact]
    public void Validate_AcceptsTheDefaults()
    {
        new PgOutboxTableSettings().Validate();
    }

    [Fact]
    public void MigrationUseTestSettings_SurvivesTheScheduleValidator()
    {
        PgOutboxMigrationSettings outbox = new PgOutboxMigrationSettings().UseTestSettings();

        // The partitional schedule validates every named instance at host start, whatever
        // AsBackgroundJob says; the old values (0 days, zero interval) failed that check.
        new MigrationScheduleSettings
        {
            AsBackgroundJob = outbox.AsBackgroundJob,
            ForwardDays = outbox.ForwardDays,
            ExecutionInterval = outbox.ExecutionInterval,
        }.Validate();
    }

    [Fact]
    public void CleanupUseTestSettings_SurvivesTheScheduleValidator()
    {
        PgOutboxCleanupSettings outbox = new PgOutboxCleanupSettings().UseTestSettings();

        new PartCleanupScheduleSettings
        {
            AsBackgroundJob = outbox.AsBackgroundJob,
            DropPartsAfterRetention = outbox.DropPartsAfterRetention,
            ExecutionInterval = outbox.ExecutionInterval,
        }.Validate();
    }
}
