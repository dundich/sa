using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// <see cref="BoundedStringBuilder"/> — накопитель stderr с ограничением сверху: поток
/// процесса не ограничен, отчёт об ошибке должен быть ограничен.
/// </summary>
public sealed class BoundedStringBuilderTests
{
    [Fact]
    public void ShortInput_IsKeptIntact()
    {
        var sb = new BoundedStringBuilder(capacity: 1024);
        sb.AppendLine("first");
        sb.AppendLine("second");

        Assert.Equal($"first{Environment.NewLine}second{Environment.NewLine}", sb.ToString());
        Assert.Equal("first".Length + "second".Length + 2 * Environment.NewLine.Length, sb.Length);
        Assert.DoesNotContain(BoundedStringBuilder.TruncationMarker, sb.ToString());
    }

    [Fact]
    public void Overflow_KeepsTheTailAndMarksTruncation()
    {
        var sb = new BoundedStringBuilder(capacity: 100);

        for (var i = 0; i < 1000; i++)
            sb.AppendLine($"line {i}");

        var text = sb.ToString();

        Assert.StartsWith(BoundedStringBuilder.TruncationMarker, text);
        Assert.Contains("line 999", text);      // хвост сохранился
        Assert.DoesNotContain("line 0", text);  // самое старое выброшено
    }

    [Fact]
    public void Length_StaysBoundedEvenWithLongLines()
    {
        const int capacity = 1000;
        const int lineLength = 400;

        var sb = new BoundedStringBuilder(capacity);

        for (var i = 0; i < 5000; i++)
            sb.AppendLine(new string('x', lineLength));

        // Очередь выбрасывает, пока не останется одна строка: в худшем случае превышение
        // ограничено длиной одной строки. Инвариант — рост ограничен, а не «ровно capacity».
        Assert.InRange(sb.Length, 0, capacity + lineLength + Environment.NewLine.Length);
        Assert.Contains(BoundedStringBuilder.TruncationMarker, sb.ToString());
    }
}
