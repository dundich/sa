
namespace Sa.Partitional.PostgreSql.Settings;

internal class TableSettingsStorage(IReadOnlyCollection<ITableSettings> settings) : ITableSettingsStorage
{

    public IReadOnlyCollection<string> Schemas { get; } = settings
        .Select(c => c.DatabaseSchemaName)
        .Distinct()
        .ToArray();

    public IReadOnlyCollection<ITableSettings> Tables => settings;
}
