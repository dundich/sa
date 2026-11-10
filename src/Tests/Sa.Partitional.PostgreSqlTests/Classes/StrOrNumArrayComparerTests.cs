
using Sa.Partitional.PostgreSql;
namespace Sa.Partitional.PostgreSqlTests.Classes;

/// <summary>
/// Structural equality of a list-partition branch. The adoption path used to deduplicate branches by
/// <c>string.Join("|", …)</c>, which is not injective: a value that itself contains <c>|</c> collided
/// with a pair of other values and silently dropped a branch.
/// </summary>
public class StrOrNumArrayComparerTests
{
    private static StrOrNum[] Branch(params StrOrNum[] values) => values;

    [Fact]
    public void EqualBranches_AreEqual_AndShareAHash()
    {
        StrOrNum[] first = Branch("tenant", "EU");
        StrOrNum[] second = Branch("tenant", "EU");

        Assert.False(ReferenceEquals(first, second));
        Assert.True(StrOrNumArrayComparer.Instance.Equals(first, second));
        Assert.Equal(
            StrOrNumArrayComparer.Instance.GetHashCode(first),
            StrOrNumArrayComparer.Instance.GetHashCode(second));
    }

    [Fact]
    public void DifferentBranches_AreNotEqual()
    {
        Assert.False(StrOrNumArrayComparer.Instance.Equals(Branch("a"), Branch("b")));
        Assert.False(StrOrNumArrayComparer.Instance.Equals(Branch("a", "b"), Branch("a")));
        Assert.False(StrOrNumArrayComparer.Instance.Equals(Branch("a", 1), Branch("a", 2)));
    }

    /// <summary>
    /// The collision the string key produced: <c>["a|s:b", "c"]</c> and <c>["a", "b|s:c"]</c> both
    /// rendered as <c>s:a|s:b|s:c</c>, so one of the two branches was dropped.
    /// </summary>
    [Fact]
    public void BranchesThatCollidedUnderAJoinedStringKey_AreDistinct()
    {
        StrOrNum[] first = Branch("a|s:b", "c");
        StrOrNum[] second = Branch("a", "b|s:c");

        Assert.Equal(string.Join("|", first.Select(v => v.ToFmtString())), string.Join("|", second.Select(v => v.ToFmtString())));
        Assert.False(StrOrNumArrayComparer.Instance.Equals(first, second));
    }

    [Fact]
    public void Distinct_KeepsBothBranches()
    {
        StrOrNum[][] branches = [Branch("a|s:b", "c"), Branch("a", "b|s:c"), Branch("a|s:b", "c")];

        StrOrNum[][] distinct = [.. branches.Distinct(StrOrNumArrayComparer.Instance)];

        Assert.Equal(2, distinct.Length);
    }
}
