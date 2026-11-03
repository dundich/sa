using System.Text;

namespace Sa.Media.FFmpeg.Services;

/// <summary>
/// Накопитель строк с верхней границей по размеру: при переполнении выбрасывает самые старые
/// строки и помечает усечение. Нужен там, где поток вывода процесса не ограничен по объёму —
/// stderr FFmpeg на битом потоке это сотни мегабайт, а в отчёт об ошибке попадает только хвост.
/// </summary>
internal sealed class BoundedStringBuilder(int capacity)
{
    public const string TruncationMarker = "... (truncated)";

    readonly Queue<string> _lines = new();
    int _length;
    bool _truncated;

    /// <summary>Сколько символов сейчас сохранено (без маркера усечения).</summary>
    public int Length => _length;

    public void AppendLine(string line)
    {
        _lines.Enqueue(line);
        _length += line.Length + Environment.NewLine.Length;

        while (_length > capacity && _lines.Count > 1)
        {
            _length -= _lines.Dequeue().Length + Environment.NewLine.Length;
            _truncated = true;
        }
    }

    public override string ToString()
    {
        if (!_truncated) return string.Concat(_lines.Select(l => l + Environment.NewLine));

        var sb = new System.Text.StringBuilder(capacity + TruncationMarker.Length + Environment.NewLine.Length);
        sb.AppendLine(TruncationMarker);
        foreach (var line in _lines) sb.AppendLine(line);
        return sb.ToString();
    }
}
