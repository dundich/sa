using Sa.Outbox.PostgreSql.Configuration;

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
    }
}
