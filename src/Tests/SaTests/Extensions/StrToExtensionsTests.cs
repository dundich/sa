using Sa.Extensions;
using System.Globalization;

namespace SaTests.Extensions;

/// <summary>
/// Регрессия на <c>StrToEnum</c> и <c>StrToDate</c>.
/// </summary>
public class StrToExtensionsTests
{
    #region StrToEnum

    /// <summary>Обычная (не flags) перечисление.</summary>
    public enum Level
    {
        None = 0,
        Low = 1,
        High = 2,
    }

    [Flags]
    public enum Access
    {
        None = 0,
        Read = 1,
        Write = 2,
        All = Read | Write,
    }

    [Theory]
    [InlineData("Low", Level.Low)]
    [InlineData("low", Level.Low)]          // регистр не важен
    [InlineData("HIGH", Level.High)]
    [InlineData(" None ", Level.None)]      // пробелы вокруг имени
    public void StrToEnum_MemberName_ParsesIgnoringCase(string input, Level expected)
    {
        Assert.Equal(expected, input.StrToEnum(Level.None));
    }

    [Theory]
    [InlineData("7")]            // числа не соответствуют ни одному члену
    [InlineData("3")]            // Low|High — комбинации нет, член не определён
    [InlineData("-1")]
    [InlineData("Read, Write")]  // список имён для перечисления без [Flags]
    public void StrToEnum_UndefinedValue_FallsBackToDefault(string input)
    {
        // Arrange
        // Enum.TryParse принимает и числа, и списки имён независимо от [Flags], поэтому
        // получалось значение, которого нет среди членов: оно проходило мимо всех
        // ветвей switch у вызывающего.

        // Act & Assert
        Assert.Equal(Level.None, input.StrToEnum(Level.None));
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void StrToEnum_Unparsable_FallsBackToDefault(string? input)
    {
        Assert.Equal(Level.Low, input.StrToEnum(Level.Low));
    }

    [Theory]
    [InlineData("Read", Access.Read)]
    [InlineData("Read, Write", Access.All)]   // комбинации флагов разрешены
    [InlineData("3", Access.All)]             // числовая форма для [Flags] допустима
    public void StrToEnum_Flags_AcceptsCombinationsAndNumbers(string input, Access expected)
    {
        Assert.Equal(expected, input.StrToEnum(Access.None));
    }

    [Fact]
    public void StrToEnum_Flags_UnknownName_FallsBackToDefault()
    {
        Assert.Equal(Access.Read, "Nope".StrToEnum(Access.Read));
    }

    #endregion

    #region StrToDate

    [Theory]
    [InlineData("2024-01-01 10:00:00", 2024, 1, 1, 10, 0, 0)]
    [InlineData("2024-01-01T10:00:00", 2024, 1, 1, 10, 0, 0)]
    [InlineData("20240101", 2024, 1, 1, 0, 0, 0)]
    public void StrToDate_WithoutOffset_IsReadAsLocalWallClock(string input, int y, int mo, int d, int h, int mi, int s)
    {
        // Act
        var result = input.StrToDate();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Local, result.Value.Kind);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), result.Value);
    }

    [Theory]
    [InlineData("2024-01-01T10:00:00+03:00")]   // 07:00 UTC
    [InlineData("2024-01-01T07:00:00Z")]        // 07:00 UTC
    [InlineData("2024-01-01T04:00:00-03:00")]   // 07:00 UTC
    public void StrToDate_WithOffset_PreservesTheInstantAndStaysLocal(string input)
    {
        // Act
        var result = input.StrToDate();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Local, result.Value.Kind);

        // Момент один и тот же независимо от часового пояса машины; Local означает, что
        // стенное время — местное, поэтому сравниваем именно момент, а не показания часов.
        Assert.Equal(new DateTime(2024, 1, 1, 7, 0, 0, DateTimeKind.Utc), result.Value.ToUniversalTime());
    }

    [Fact]
    public void StrToDate_EveryFormatShape_YieldsTheSameKind()
    {
        // Исходный дефект: список форматов смешивает формы без смещения (дают Unspecified)
        // и формы со смещением (дают Local), поэтому Kind зависел от того, какой формат
        // случайно совпал. Теперь он одинаков для всех.
        const string withoutOffset = "2024-01-01 10:00:00";
        const string withOffset = "2024-01-01T10:00:00+03:00";

        var a = withoutOffset.StrToDate();
        var b = withOffset.StrToDate();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(DateTimeKind.Local, a.Value.Kind);
        Assert.Equal(a.Value.Kind, b.Value.Kind);
    }

    [Fact]
    public void StrToDate_OffsetLessInput_KeepsItsWallClockReading()
    {
        const string input = "2024-01-01 10:00:00";

        var result = input.StrToDate();

        Assert.NotNull(result);
        Assert.Equal(10, result.Value.Hour);
        Assert.Equal(
            new DateTimeOffset(result.Value).Offset,
            TimeZoneInfo.Local.GetUtcOffset(result.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не дата")]
    [InlineData("2024-13-45")]
    public void StrToDate_Unparsable_ReturnsNull(string? input)
    {
        Assert.Null(input.StrToDate());
    }

    [Fact]
    public void StrToDate_ExplicitAssumeLocal_IsAccepted()
    {
        // AssumeLocal уже входит в нормализацию, поэтому и явная передача не конфликтует.
        var result = "2024-01-01 10:00:00".StrToDate(style: DateTimeStyles.AssumeLocal);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Local, result.Value.Kind);
    }

    [Fact]
    public void StrToDate_SpanOverload_ReturnsSameResultAsStringOverload()
    {
        const string input = "2024-01-01T10:00:00+03:00";

        Assert.Equal(input.StrToDate(), input.AsSpan().StrToDate());
    }

    #endregion
}
