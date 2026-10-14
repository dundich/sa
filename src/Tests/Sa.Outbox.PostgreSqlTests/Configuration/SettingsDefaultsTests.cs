using Sa.Outbox.PostgreSql.Configuration;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Freezes the real defaults of <see cref="PgOutboxCleanupSettings"/> so any
/// silent change of the property initializers is caught by the test.
/// </summary>
public class CleanupSettingsDefaultsTests
{
    [Fact]
    public void NewInstance_Defaults_AreAsDocumented()
    {
        var settings = new PgOutboxCleanupSettings();

        Assert.True(settings.AsBackgroundJob);
        Assert.Equal(TimeSpan.FromDays(30), settings.DropPartsAfterRetention);
        Assert.Equal(TimeSpan.FromHours(4), settings.ExecutionInterval);
        Assert.Equal(PartCleanupMode.Drop, settings.CleanupMode);
        Assert.Equal("archive", settings.ArchiveSchemaName);
    }

    [Fact]
    public void CleanupMode_CanBeSetToArchive()
    {
        var settings = new PgOutboxCleanupSettings
        {
            CleanupMode = PartCleanupMode.Archive,
            ArchiveSchemaName = "cold_storage"
        };

        Assert.Equal(PartCleanupMode.Archive, settings.CleanupMode);
        Assert.Equal("cold_storage", settings.ArchiveSchemaName);
    }
}

/// <summary>
/// Freezes the real defaults of <see cref="PgOutboxMigrationSettings"/> so any
/// silent change of the property initializers is caught by the test.
/// </summary>
public class MigrationSettingsDefaultsTests
{
    [Fact]
    public void NewInstance_Defaults_AreAsDocumented()
    {
        var settings = new PgOutboxMigrationSettings();

        Assert.True(settings.AsBackgroundJob);
        Assert.Equal(2, settings.ForwardDays);

        // 6 hours + jitter (1..58 min): total must fall inside [6h, 7h]
        Assert.True(settings.ExecutionInterval >= TimeSpan.FromHours(6),
            $"Expected >= 6h but got {settings.ExecutionInterval}");
        Assert.True(settings.ExecutionInterval < TimeSpan.FromHours(7),
            $"Expected < 7h but got {settings.ExecutionInterval}");
    }
}

/// <summary>
/// Freezes the real defaults of <see cref="PgOutboxTableSettings"/> —
/// in particular the new <c>UseDefaultPartition</c> flag that gates
/// <c>WithDefaultPartition()</c> on every outbox table.
/// </summary>
public class TableSettingsDefaultsTests
{
    [Fact]
    public void NewInstance_Defaults_AreAsDocumented()
    {
        var settings = new PgOutboxTableSettings();

        Assert.Equal(PgOutboxTableSettings.Defaults.DatabaseSchemaName, settings.DatabaseSchemaName);
        Assert.Equal("public", settings.DatabaseSchemaName);

        // UseDefaultPartition must default to false — the previous behaviour
        // (no catch-all partitions) is preserved unless explicitly opted in.
        Assert.False(settings.UseDefaultPartition);

        // Sanity: the flag is settable
        settings.UseDefaultPartition = true;
        Assert.True(settings.UseDefaultPartition);
    }
}
