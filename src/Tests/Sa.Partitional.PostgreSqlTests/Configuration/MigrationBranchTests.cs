using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Configuration.Builder;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// The explicit <c>AddMigration</c> contract: one array is one branch, several branches are separate
/// arguments, a single value with children is the two-level shorthand — and a branch whose length
/// does not match the list levels is rejected by <c>Build()</c> instead of failing from a job.
/// </summary>
public class MigrationBranchTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static ITableSettings Build(Action<ITableBuilder> configure, int listLevels)
    {
        SchemaBuilder schema = new("public");

        List<string> fields = ["id bigint NOT NULL"];
        for (int i = 0; i < listLevels; i++) fields.Add($"p{i} text NOT NULL");
        fields.Add("created_at bigint NOT NULL");

        ITableBuilder table = schema.AddTable("t", [.. fields]);

        if (listLevels > 0)
        {
            table.PartByList([.. Enumerable.Range(0, listLevels).Select(i => $"p{i}")]);
        }

        table.PartByRange(PgPartBy.Day, "created_at");

        configure(table);

        return Assert.Single(schema.Build());
    }

    private static async Task<string[]> Branches(ITableSettings settings)
        => [.. (await settings.Migration.GetPartsAsync(Ct)).Select(branch => string.Join("|", branch.Select(v => v.ToString())))];

    [Fact]
    public async Task OneArray_IsExactlyOneBranch()
        => Assert.Equal(["a|b"], await Branches(Build(t => t.AddMigration(["a", "b"]), listLevels: 2)));

    [Fact]
    public async Task SeveralArrays_AreSeveralBranches()
        => Assert.Equal(
            ["a|b", "a|c"],
            await Branches(Build(t => t.AddMigration(["a", "b"], ["a", "c"]), listLevels: 2)));

    [Fact]
    public async Task ParentPlusChildren_IsTheTwoLevelShorthand()
        => Assert.Equal(
            ["a|b", "a|c", "a|d"],
            await Branches(Build(t => t.AddMigrationChildren("a", "b", "c", "d"), listLevels: 2)));

    [Fact]
    public void ChildrenWithoutValues_IsRejected()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => Build(t => t.AddMigrationChildren("a"), listLevels: 2));

        Assert.Contains("at least one child", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tree_MirrorsThePartitionTree()
    {
        ITableSettings settings = Build(t => t.AddMigrationTree(tree => tree
            .Node("RU", ru => ru
                .Branch("Moscow")
                .Branch("Kazan"))
            .Node("US", us => us
                .Branch("New York"))), listLevels: 2);

        Assert.Equal(["RU|Moscow", "RU|Kazan", "US|New York"], await Branches(settings));
    }

    [Fact]
    public async Task Tree_SupportsAnyDepth()
    {
        ITableSettings settings = Build(t => t.AddMigrationTree(tree => tree
            .Node("EU", eu => eu
                .Node("DE", de => de
                    .Branch("Berlin")
                    .Branch("Munich"))
                .Branch("FR", "Paris"))), listLevels: 3);

        Assert.Equal(["EU|DE|Berlin", "EU|DE|Munich", "EU|FR|Paris"], await Branches(settings));
    }

    [Fact]
    public async Task Tree_FlatBranch_IsAOneLevelList()
        => Assert.Equal(
            ["acme"],
            await Branches(Build(t => t.AddMigrationTree(tree => tree.Branch("acme")), listLevels: 1)));

    [Fact]
    public async Task Tree_Empty_DeclaresNothing()
        => Assert.Empty(await Branches(Build(t => t.AddMigrationTree(_ => { }), listLevels: 2)));

    [Fact]
    public void Tree_BranchWithTheWrongDepth_FailsAtBuild()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Build(
                t => t.AddMigrationTree(tree => tree.Node("a", n => n.Node("b", m => m.Branch("c")))),
                listLevels: 2));

        Assert.Contains("3 value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneValue_IsOneBranchOfAOneLevelList()
        => Assert.Equal(["tenant"], await Branches(Build(t => t.AddMigration(["tenant"]), listLevels: 1)));

    [Fact]
    public async Task NoArguments_IsANoOp()
        => Assert.Empty(await Branches(Build(t => t.AddMigration(), listLevels: 2)));

    [Fact]
    public void BranchWithTheWrongNumberOfValues_FailsAtBuild()
    {
        // The call that used to "work" by binding to the params overload: one value for a two-level
        // list is a configuration error, and it is reported while the declaration is identifiable.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Build(t => t.AddMigration(["only-one"]), listLevels: 2));

        Assert.Contains("migration branch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 list partition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesOnARangeOnlyTable_FailAtBuild()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Build(t => t.AddMigration(["tenant"]), listLevels: 0));

        Assert.Contains("range-partitioned only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArgumentsOnARangeOnlyTable_IsStillAllowed()
        => Assert.Empty(await Branches(Build(t => t.AddMigration(), listLevels: 0)));

    [Fact]
    public async Task DeclaredArray_IsCopied_SoLaterMutationDoesNotRewriteTheSnapshot()
    {
        StrOrNum[] branch = ["a", "b"];

        ITableSettings settings = Build(t => t.AddMigration(branch), listLevels: 2);

        branch[0] = "mutated";

        Assert.Equal(["a|b"], await Branches(settings));
    }
}
