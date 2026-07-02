using System.Text.RegularExpressions;
using Sa.Classes;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Encapsulates a PostgreSQL partitioning strategy (day / month / year) together with the functions
/// needed to compute date ranges, format partition names, and parse them back.
/// </summary>
/// <param name="PartByRange">The underlying <see cref="PartByRange"/> enum value.</param>
/// <param name="GetRange">Computes the inclusive-exclusive <see cref="LimSection{T}"/> for a given date.</param>
/// <param name="Fmt">Formats a <see cref="DateTimeOffset"/> into a PostgreSQL-compatible partition name.</param>
/// <param name="ParseFmt">Parses a partition name string back into a <see cref="DateTimeOffset"/>.</param>
public sealed record PgPartBy(
    PartByRange PartByRange
    , Func<DateTimeOffset, LimSection<DateTimeOffset>> GetRange
    , Func<DateTimeOffset, string> Fmt
    , Func<string, DateTimeOffset?> ParseFmt
) : Enumeration<PgPartBy>((int)PartByRange, PartByRange.ToString())
{

    /// <summary>
    /// Day-based partitioning (<c>yYYYYmmDD</c>).
    /// </summary>
    public static readonly PgPartBy Day = new(
        PartByRange: PartByRange.Day
        , GetRange: static date => date.ToUniversalTime().StartOfDay().RangeTo(date => date.AddDays(1), false)
        , Fmt: static ts => $"y{ts.Year:0000}m{ts.Month:00}d{ts.Day:00}"
        , ParseFmt: static str => StrToDate(str, PartByRange.Day)
    );

    /// <summary>
    /// Month-based partitioning (<c>yYYYYmm</c>).
    /// </summary>
    public static readonly PgPartBy Month = new(
        PartByRange: PartByRange.Month
        , GetRange: static date => date.ToUniversalTime().StartOfMonth().RangeTo(date => date.AddMonths(1), false)
        , Fmt: static ts => $"y{ts.Year:0000}m{ts.Month:00}"
        , ParseFmt: static str => StrToDate(str, PartByRange.Month)
    );

    /// <summary>
    /// Year-based partitioning (<c>yYYYY</c>).
    /// </summary>
    public static readonly PgPartBy Year = new(
        PartByRange: PartByRange.Year
        , GetRange: static date => date.ToUniversalTime().StartOfYear().RangeTo(date => date.AddYears(1), false)
        , Fmt: static ts => $"y{ts.Year:0000}"
        , ParseFmt: static str => StrToDate(str, PartByRange.Year)
    );



    #region methods

    /// <summary>
    /// Resolves a <see cref="PgPartBy"/> instance from a <see cref="PartByRange"/> enum value.
    /// Falls back to <see cref="Day"/> when the range is not found.
    /// </summary>
    /// <param name="range">The partitioning range to look up.</param>
    /// <returns>The matching <see cref="PgPartBy"/>, or <see cref="Day"/> as default.</returns>
    public static PgPartBy FromRange(PartByRange range)
        => GetAll().FirstOrDefault(c => c.PartByRange == range) ?? Day;

    /// <summary>
    /// Resolves a <see cref="PgPartBy"/> instance from a partition name (e.g. <c>"root"</c>, <c>"day"</c>).
    /// </summary>
    /// <param name="part">The display name of the partition kind.</param>
    /// <returns>The corresponding <see cref="PgPartBy"/>.</returns>
    public static PgPartBy FromPartName(string part)
    {
        Part current = Part.TryFromName(part, out Part? item) ? item : Part.Root;
        return FromRange(current.PartBy);
    }

    /// <summary>
    /// Returns the human-readable name of this partitioning strategy.
    /// </summary>
    public override string ToString() => Name;


    private static DateTimeOffset? StrToDate(string str, PartByRange range)
    {
        if (string.IsNullOrWhiteSpace(str)) return null;

        ReadOnlySpan<char> span = str.AsSpan();

        return range switch
        {
            PartByRange.Day => FmtRegex.GetDayRegEx().IsMatch(span) ? new DateTimeOffset(span[^10..^6].StrToInt()!.Value, span[^5..^3].StrToInt()!.Value, span[^2..].StrToInt()!.Value, 0, 0, 0, TimeSpan.Zero) : null,
            PartByRange.Month => FmtRegex.GetMonthRegEx().IsMatch(span) ? new DateTimeOffset(span[^7..^3].StrToInt()!.Value, span[^2..].StrToInt()!.Value, 1, 0, 0, 0, TimeSpan.Zero) : null,
            PartByRange.Year => FmtRegex.GetYearRegEx().IsMatch(span) ? new DateTimeOffset(span[^4..].StrToInt()!.Value, 1, 1, 0, 0, 0, TimeSpan.Zero) : null,
            _ => null,
        };
    }

    #endregion
}


static partial class FmtRegex
{
    [GeneratedRegex(@".*y(\d{4})m(\d{2})d(\d{2})$")]
    public static partial Regex GetDayRegEx();

    [GeneratedRegex(@".*y(\d{4})m(\d{2})$")]
    public static partial Regex GetMonthRegEx();

    [GeneratedRegex(@".*y(\d{4})$")]
    public static partial Regex GetYearRegEx();
}
