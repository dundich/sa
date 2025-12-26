using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Represents information about a partition in a database table based on a range of values.
/// </summary>
/// <param name="Id">The unique identifier for the partition - fully qualified name of the database table, including the schema.</param>
/// <param name="RootTableName">The name of the original table from which this partition is derived.</param>
/// <param name="PartValues">An array of values that define the partitioning criteria, which can be either string or numeric.</param>
/// <param name="PartBy">The method used for partitioning (e.g., by range, list, etc.).</param>
/// <param name="FromDate">The date from which this partition is valid.</param>
public record PartByRangeInfo(string Id, string RootTableName, StrOrNum[] PartValues, PgPartBy PartBy, DateTimeOffset FromDate);

/// <summary>
/// Represents a repository interface for managing database partitions.
/// This interface defines methods for creating, migrating, retrieving, and dropping partitions in a database.
/// </summary>
public interface IPartRepository
{
    Task<int> CreatePart(string tableName, DateTimeOffset date, StrOrNum[] partValues, CancellationToken cancellationToken = default);
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);
    Task<int> Migrate(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve, CancellationToken cancellationToken = default);
    Task<List<PartByRangeInfo>> GetPartsFromDate(string tableName, DateTimeOffset fromDate, CancellationToken cancellationToken = default);
    Task<List<PartByRangeInfo>> GetPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default);
    Task<int> DropPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default);
}
