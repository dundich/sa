
namespace Sa.Partitional.PostgreSql;

public interface ITableSettingsStorage
{
    IReadOnlyCollection<string> Schemas { get; }
    IReadOnlyCollection<ITableSettings> Tables { get; }
}
