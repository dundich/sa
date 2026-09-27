using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests.Classes;

/// <summary>
/// Unit tests for the enumeration base: a name lookup has to tolerate what actually comes out of the
/// database, and an id used for comparison must not change from process to process.
/// </summary>
public class EnumerationTests
{
    [Fact]
    public void TryFromName_ReportsNullAsNotFound_InsteadOfThrowing()
    {
        // The part_by column is read as a nullable string, and a NULL used to reach the dictionary,
        // where the caller expected a plain "no match" and got an ArgumentNullException instead.
        Assert.False(Part.TryFromName(null, out Part? part));
        Assert.Null(part);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("DAY")]
    public void TryFromName_ReportsAnUnregisteredNameAsNotFound(string? name)
    {
        Assert.False(Part.TryFromName(name, out _));
    }

    [Fact]
    public void TryFromName_FindsTheRegisteredVariant()
    {
        Assert.True(Part.TryFromName(Part.RootId, out Part? root));
        Assert.Equal(Part.Root, root);
    }

    [Fact]
    public void FromName_StillThrows_ForAnUnregisteredName()
    {
        // Only the Try* path learned to tolerate null: the strict counterpart keeps its contract.
        Assert.Throws<InvalidOperationException>(() => Part.FromName("nonsense"));
    }

    [Fact]
    public void Part_Id_IsTheSame_ForEveryInstanceWithTheSameName()
    {
        // string.GetHashCode() is randomised per process, so two parts with the same name could carry
        // different ids and stop matching each other.
        Part first = new("day", PartByRange.Day);
        Part second = new("day", PartByRange.Day);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(0, Enumeration<Part>.DiffId(first, second));
    }

    [Fact]
    public void Part_Id_IsPinned_ToAProcessIndependentValue()
    {
        // Pinned on purpose: 553455173 is FNV-1a over the UTF-16 code units of "root". If the id
        // derivation ever changes, every id that is compared or stored changes with it - and that
        // has to show up here as a failing test rather than as different behaviour in production.
        Assert.Equal(553455173, Part.Root.Id);
    }

    [Fact]
    public void Part_Id_IsDistinct_PerVariant()
    {
        int[] ids = [.. new[] { "root", "day", "month", "year" }.Select(name => new Part(name, PartByRange.Day).Id)];

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
