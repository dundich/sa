using Sa.Partitional.PostgreSql.SqlBuilder;
using System.Text;

namespace Sa.Partitional.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests for <see cref="SqlTemplate.TrimIdentifier"/>: the library-derived names (adoption backup
/// and archived-copy replacements) are truncated to make room for their own suffix, and PostgreSQL
/// counts identifier length in UTF-8 <b>bytes</b>, on a character boundary.
/// </summary>
public class TrimIdentifierTests
{
    [Fact]
    public void NameThatFits_IsReturnedUnchanged()
    {
        string name = new('a', 63);

        Assert.Equal(name, SqlTemplate.TrimIdentifier(name));
        Assert.Equal(63, Encoding.UTF8.GetByteCount(SqlTemplate.TrimIdentifier(name)));
    }

    [Fact]
    public void LongName_IsCutToTheByteBudget()
    {
        string trimmed = SqlTemplate.TrimIdentifier(new string('a', 70), 10);

        Assert.Equal(new string('a', 10), trimmed);
    }

    [Fact]
    public void MultiByteName_IsCutOnARuneBoundary()
    {
        // 'é' is two UTF-8 bytes and one char: a 5-byte budget fits two runes, not two and a half.
        string trimmed = SqlTemplate.TrimIdentifier("éééé", 5);

        Assert.Equal("éé", trimmed);
        Assert.True(Encoding.UTF8.GetByteCount(trimmed) <= 5);
    }

    [Fact]
    public void EmojiSurrogatePair_IsNeverSplit()
    {
        // U+1F600 is one rune, four UTF-8 bytes and two chars; a 4-byte budget fits exactly one.
        string trimmed = SqlTemplate.TrimIdentifier("😀😀", 4);

        Assert.Equal("😀", trimmed);
        Assert.True(Encoding.UTF8.GetByteCount(trimmed) <= 4);
    }

    [Fact]
    public void BudgetSmallerThanTheFirstRune_YieldsAnEmptyName()
    {
        Assert.Equal(string.Empty, SqlTemplate.TrimIdentifier("😀", 3));
    }
}
