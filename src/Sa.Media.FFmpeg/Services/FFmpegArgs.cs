namespace Sa.Media.FFmpeg.Services;

/// <summary>
/// Построение аргументов командной строки FFmpeg.
/// <para>
/// Всё, что приходит от вызывающего кода, попадает в строку <c>ProcessStartInfo.Arguments</c>.
/// Разбор этой строки делает не FFmpeg, а .NET — и одинаково на всех ОС, по правилам Windows
/// (двойные кавычки + backslash-экранирование). Поэтому и экранировать нужно по этим правилам:
/// одинарные кавычки FFmpeg на Unix не работают.
/// </para>
/// </summary>
internal static class FFmpegArgs
{
    /// <summary>
    /// Имена форматов/демуксеров FFmpeg — идентификаторы из букв, цифр и подчёркиваний
    /// ("wav", "mp3", "s16le", "f32le", "matroska", ...). Ничего другого в -f передавать нельзя:
    /// иначе строка вида <c>wav -i /etc/passwd</c> разбирается как отдельный входной файл.
    /// </summary>
    public static void ValidateFormatName(string? value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Format name must not be empty", paramName);

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException(
                    $"'{value}' is not a valid FFmpeg format name. Expected letters, digits and underscores " +
                    "(for example: wav, mp3, ogg, s16le, f32le).",
                    paramName);
        }
    }

    /// <summary>Суффикс имён выходных файлов — тоже идентификатор, а не произвольная строка.</summary>
    public static void ValidateFileNameToken(string? value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Value must not be empty", paramName);

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
                throw new ArgumentException(
                    $"'{value}' may contain only letters, digits, '_', '-' and '.'.", paramName);
        }
    }

    /// <summary>
    /// Возвращает <paramref name="value"/>, готовый к подстановке в командную строку.
    /// Удобно там, где команда собирается интерполяцией, а не через <see cref="FFCmd"/>.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var b = new ValueStringBuilder(value.Length + 2);
        AppendQuoted(ref b, value);
        return b.ToString();
    }

    /// <summary>
    /// Дописывает <paramref name="value"/> в командную строку как один аргумент в кавычках.
    /// Работает на всех ОС, потому что использует те же правила, что и разбор .NET.
    /// <para>
    /// Обязательный <c>ref</c>: <see cref="ValueStringBuilder"/> — изменяемая структура с
    /// отдельным полем позиции. Переданная по значению копия пишет символы в общий буфер, но
    /// позицию вызывающего не двигает — и результат молча обрезается. Такой баг не ловится
    /// компилятором, поэтому параметр по значению здесь считается ошибкой.
    /// </para>
    /// </summary>
    public static void AppendQuoted(scoped ref ValueStringBuilder b, string value)
    {
        b.Append('"');

        // Правило ровно то, что реализует CommandLineToArgvW (и .NET при разборе Arguments
        // на любой ОС):
        //   * серия из 2n слэшей перед кавычкой  -> n слэшей, кавычка переключает режим;
        //   * серия из 2n+1 слэшей перед кавычкой -> n слэшей, кавычка литеральная;
        //   * серия слэшей перед обычным символом  -> сохраняется как есть.
        //
        // Отсюда две детали, на которых легко ошибиться:
        //   * удваивать нужно ТОЛЬКО слэши перед кавычкой. Если удвоить все, то «a\b.wav»
        //     превратится в «a\\b.wav», и FFmpeg получит имя с лишним слэшем — то есть
        //     не найдёт файл, который существует;
        //   * перед литеральной кавычкой нужно 2n+1 слэшей, а значит для n слэшей в самом
        //     значении выводится 2n+1, а не n+1.
        var pending = 0;

        foreach (var c in value)
        {
            if (c == '\\')
            {
                pending++;
                continue;
            }

            // 2n+1 слэшей перед кавычкой разбираются как n слэшей и литеральная кавычка.
            b.Append('\\', c == '"' ? pending * 2 + 1 : pending);
            pending = 0;
            b.Append(c);
        }

        // Хвост: закрывающая кавычка обязана переключить режим, поэтому слэшей должно быть
        // чётное число — ровно вдвое больше, чем в значении.
        b.Append('\\', pending * 2);
        b.Append('"');
    }
}
