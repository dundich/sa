using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests (no database) for the 63-byte budget of a generated partition name: PostgreSQL truncates
/// identifiers at 63 bytes, so a consumer group id or a message part name that is too long makes the
/// leaf DDL fail — for the delivery log, on the first <i>failed</i> delivery.
/// </summary>
public class PgOutboxPartitionNameBudgetTests
{
    [Fact]
    public void Separator_MatchesThePartitionalDefault()
    {
        // The budget arithmetic in PgOutboxPartitionNames relies on the separator the partitional
        // module actually uses. The Outbox never configures WithPartSeparator, so the two constants
        // have to stay equal - and this is the test that says so.
        var services = new ServiceCollection();
        // The partitional chain resolves logging-backed services even when only the settings are built.
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddSaPartitional((_, builder) => builder.AddSchema(schema =>
            schema.AddTable("budget_probe", "id INT NOT NULL")));

        using ServiceProvider provider = services.BuildServiceProvider();

        ITableSettingsStorage storage = provider.GetRequiredService<ISettingsBuilder>().Build();
        ITableSettings settings = Assert.Single(storage.Tables);

        Assert.Equal(PgOutboxTableSettings.Defaults.SqlPartSeparator, settings.SqlPartSeparator);
    }

    [Fact]
    public void ValueBudget_GrowsWhenTheTableNameShrinks()
    {
        int byDefault = PgOutboxPartitionNames.ValueBudget("outbox__log$", 2);
        int shorter = PgOutboxPartitionNames.ValueBudget("o", 2);

        Assert.True(shorter > byDefault);

        // 63 minus name, three separators and the 11-byte day suffix.
        Assert.Equal(63 - "outbox__log$".Length - 3 * 2 - 11, byDefault);
    }

    [Fact]
    public void EnsureFits_AcceptsAValueThatFits()
    {
        PgOutboxPartitionNames.EnsureFits("outbox__log$", "tenant id, consumer group", ["1", "group"]);
    }

    [Fact]
    public void EnsureFits_NamesTheValueThatDoesNotFit()
    {
        string group = new('g', 40);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PgOutboxPartitionNames.EnsureFits("outbox__log$", "tenant id, consumer group", ["1", group]));

        Assert.Contains("63-byte", ex.Message);
        Assert.Contains(group, ex.Message);
        Assert.Contains("outbox__log$", ex.Message);
    }

    [Fact]
    public void Validate_RejectsAConsumerGroupIdThatCannotFit()
    {
        var settings = new PgOutboxTableSettings();
        string group = new('g', 40);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => settings.Validate([group]));

        Assert.Contains("does not", ex.Message);
        Assert.Contains("Consumer group id", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsAReasonableConsumerGroupId()
    {
        new PgOutboxTableSettings().Validate(["orders-consumer-v1"]);
    }

    [Fact]
    public void Validate_WithoutGroups_SkipsTheBudgetCheck()
    {
        // A publish-only deployment registers no delivery groups: null means "nothing to check".
        new PgOutboxTableSettings().Validate(null);
    }
}
