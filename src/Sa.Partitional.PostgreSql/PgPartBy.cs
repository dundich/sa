using Sa.Classes;
using Sa.Extensions;
using System.Text.RegularExpressions;

namespace Sa.Partitional.PostgreSql;

public record PgPartBy(
    PartByRange PartByRange
    , Func<DateTimeOffset, LimSection<DateTimeOffset>> GetRange
    , Func<DateTimeOffset, string> Fmt
    , Func<string, DateTimeOffset?> ParseFmt
)
    : Enumeration<PgPartBy>((int)PartByRange, PartByRange.ToString())
{

    public static readonly PgPartBy Day = new(
        PartByRange: PartByRange.Day
        , GetRange: static date => date.ToUniversalTime().StartOfDay().RangeTo(date => date.AddDays(1), false)
        , Fmt: static ts => $"y{ts.Year:0000}m{ts.Month:00}d{ts.Day:00}"
        , ParseFmt: static str => StrToDate(str, PartByRange.Day)
    );

    public static readonly PgPartBy Month = new(
        PartByRange: PartByRange.Month
        , GetRange: static date => date.ToUniversalTime().StartOfMonth().RangeTo(date => date.AddMonths(1), false)
        , Fmt: static ts => $"y{ts.Year:0000}m{ts.Month:00}"
        , ParseFmt: static str => StrToDate(str, PartByRange.Month)
    );

    public static readonly PgPartBy Year = new(
        PartByRange: PartByRange.Year
        , GetRange: static date => date.ToUniversalTime().StartOfYear().RangeTo(date => date.AddYears(1), false)
        , Fmt: static ts => $"y{ts.Year:0000}"
        , ParseFmt: static str => StrToDate(str, PartByRange.Year)
    );



    #region methods

    public static PgPartBy FromRange(PartByRange range)
        => GetAll().FirstOrDefault(c => c.PartByRange == range) ?? Day;

    public static PgPartBy FromPartName(string part)
    {
        Part current = Part.TryFromName(part, out Part? item) ? item : Part.Root;
        return FromRange(current.PartBy);
    }

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
