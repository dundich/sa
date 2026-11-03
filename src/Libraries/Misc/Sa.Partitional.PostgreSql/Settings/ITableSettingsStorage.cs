
namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Immutable snapshot of all schemas and tables produced by <see cref="ISettingsBuilder.Build"/>.
/// </summary>
public interface ITableSettingsStorage
{
    /// <summary>
    /// Gets the set of schema names that were registered (includes the default schema if no custom schemas were added).
    /// </summary>
    IReadOnlyCollection<string> Schemas { get; }

    /// <summary>
    /// Gets the complete collection of table settings across all schemas.
    /// </summary>
    IReadOnlyCollection<ITableSettings> Tables { get; }
}
