using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal sealed class SqlTableBuilder(ITableSettings settings) : ISqlTableBuilder
{
    private readonly SqlRootBuilder rootBuilder = new(settings);
    private readonly SqlPartListBuilder partListBuilder = new(settings);
    private readonly SqlPartRangeBuilder partRangeBuilder = new(settings);

    public string FullName => settings.FullName;

    public ITableSettings Settings => settings;

    public PgPartBy PartBy => settings.PartBy;


    public string CreateSql(DateTimeOffset date, params StrOrNum[] partValues)
    {
        ArgumentNullException.ThrowIfNull(partValues);

        // The number of list levels is fixed by the configuration, so a different number of values
        // is a caller bug, not a variant of the DDL. This used to be tolerated under an
        // "-- incomplete number of parts" comment: the result was a partition tree whose deepest
        // level had no range leaf, so every INSERT for that date then failed with "no partition of
        // relation ... found for row" - although the call that produced the DDL reported success.
        if (settings.PartByListFieldNames.Length != partValues.Length)
        {
            throw new ArgumentException(
                $"Table '{settings.FullName}' has {settings.PartByListFieldNames.Length} list partition "
                + $"field(s){Describe(settings.PartByListFieldNames)}, so exactly that many partition "
                + $"values are required, but {partValues.Length} were given.",
                nameof(partValues));
        }

        // "O" (round-trip) keeps the annotating comment culture-independent, so the generated SQL
        // is byte-identical on every machine.
        return
$"""
-- {date:O}
{rootBuilder.CreateSql()}
{partListBuilder.CreateSql(partValues)}
{partRangeBuilder.CreateSql(date, partValues)}
""";
    }

    private static string Describe(string[] fieldNames)
        => fieldNames.Length == 0 ? " (the table is range-partitioned only)" : $" ({string.Join(", ", fieldNames)})";

    public override string ToString() => $"{FullName} {Settings.PartByListFieldNames}";

    public string SelectPartsFromDate { get; } = settings.SelectPartsFromDateSql();

    public string SelectPartsToDate { get; } = settings.SelectPartsToDateSql();
}
