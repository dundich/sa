using Sa.Media.FFmpeg.Services;

namespace Sa.Media.FFmpegTests;

/// <summary>
/// Проверки формирования аргументов командной строки.
/// <para>
/// Квотирование здесь не косметика: строка целиком уходит в <c>ProcessStartInfo.Arguments</c>,
/// а .NET разбирает её одинаково на всех ОС по правилам Windows. Если имя файла содержит
/// пробел, кавычку или точку с запятой, неэкранированная строка превращается в несколько
/// аргументов — и FFmpeg получает чужую команду.
/// </para>
/// </summary>
public sealed class FFmpegArgsTests
{
    /// <summary>
    /// Разбирает командную строку тем же способом, что и .NET при запуске процесса.
    /// Только для проверки: сам FFmpeg так не делает.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var backslashes = 0;

        foreach (var c in commandLine)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                current.Append('\\', backslashes / 2);
                if (backslashes % 2 == 0) inQuotes = !inQuotes;
                else current.Append('"');
                backslashes = 0;
                continue;
            }

            current.Append('\\', backslashes);
            backslashes = 0;

            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        current.Append('\\', backslashes);
        if (current.Length > 0) result.Add(current.ToString());

        return result;
    }

    [Theory]
    [InlineData("plain.wav")]
    [InlineData("with space.wav")]
    [InlineData("\"quoted\".wav")]
    [InlineData("back\\slash.wav")]
    [InlineData("trailing\\.wav")]
    [InlineData("двойной пробел  внутри.wav")]
    [InlineData("точка;с;запятой.wav")]
    public void AppendQuoted_SurvivesRoundTripThroughDotNetParsing(string fileName)
    {
        var quoted = FFmpegArgs.Quote(fileName);

        var parsed = SplitArguments($"-i {quoted}");

        var input = Assert.Single(parsed, a => !a.StartsWith('-'));
        Assert.Equal(fileName, input);
    }

    [Fact]
    public void AppendQuoted_KeepsArgumentThatLooksLikeAnOptionInsideTheFileName()
    {
        // Регрессия: без кавычек имя «-f null -» превращалось в три аргумента, и FFmpeg
        // получал команду, которую вызывающий не просил.
        var hostile = "x -f null -y /etc/passwd";
        var quoted = FFmpegArgs.Quote(hostile);

        var parsed = SplitArguments($"-i {quoted}");

        Assert.Equal(["-i", hostile], parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wav; rm -rf /")]
    [InlineData("wav -y")]
    [InlineData("wav\n-y")]
    [InlineData("-y")]
    [InlineData("a/b")]
    public void ValidateFormatName_RejectsAnythingFFmpegWouldReadAsMoreThanAFormat(string? name) =>
        Assert.Throws<ArgumentException>(() => FFmpegArgs.ValidateFormatName(name!, "formatName"));

    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("ogg")]
    [InlineData("s16le")]
    [InlineData("f32le")]
    [InlineData("matroska")]
    public void ValidateFormatName_AcceptsPlainNames(string name) =>
        FFmpegArgs.ValidateFormatName(name, "formatName");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateFileNameToken_RejectsEmpty(string? name) =>
        Assert.Throws<ArgumentException>(() => FFmpegArgs.ValidateFileNameToken(name!, "fileName"));

    [Fact]
    public void ValidateFileNameToken_AcceptsIdentifierLikeSuffixes() =>
        FFmpegArgs.ValidateFileNameToken("_channel_", "fileName");

    [Theory]
    [InlineData("my file")]      // пробел
    [InlineData("(1)")]
    [InlineData("a;b")]
    [InlineData("a/b")]         // разделитель пути
    [InlineData("../escape")]
    public void ValidateFileNameToken_RejectsAnythingThatCouldChangeTheResultingPath(string suffix) =>
        // Суффикс подставляется в имя выходного файла, поэтому это не «просто строка»:
        // разделитель пути или точка с запятой увели бы имя файла за пределы ожидаемого.
        Assert.Throws<ArgumentException>(() => FFmpegArgs.ValidateFileNameToken(suffix, "fileName"));
}
