using Sa.Classes;
using Sa.Extensions;

namespace SaTests.Classes;

/// <summary>
/// Регрессия на неограниченный <c>stackalloc</c>. Стек потока ограничен, а
/// <see cref="StackOverflowException"/> перехватить нельзя — процесс падает целиком.
/// Тесты гоняются на отдельном потоке с 256 КБ стека, как у потоков ASP.NET; квадратичные
/// алгоритмы — на 64 КБ, чтобы проверка не стоила десятков секунд (см.
/// <see cref="SmallStackBytesForQuadraticWork"/>).
/// </summary>
public class StackallocTests
{
    private const int ThreadStackBytes = 256 * 1024;

    /// <summary>
    /// Стек для квадратичных алгоритмов. Наивный <c>stackalloc</c> трёх полос по 6 001 int —
    /// это 3 * 6 001 * 4 = 72 КБ, то есть 64 КБ стека по-прежнему переполняются, и тест
    /// ловит ту же регрессию, что и на 256 КБ. Зато 6 000 × 6 000 ячеек считаются за доли
    /// секунды, тогда как 60 000 × 60 000 — это 3.6 млрд итераций: ~21 с в Release и заметно
    /// дольше в Debug, где 60-секундный <see cref="Thread.Join(TimeSpan)"/> превращал
    /// медленный прогон в «test thread did not finish».
    /// </summary>
    private const int SmallStackBytesForQuadraticWork = 64 * 1024;

    /// <summary>
    /// Выполняет <paramref name="action"/> на потоке с маленьким стеком и возвращает результат.
    /// Поток намеренно переживает падение: без этого упавший StackOverflowException убил бы
    /// весь тестовый процесс, и падение выглядело бы как «тесты зависли».
    /// </summary>
    private static Exception? RunOnSmallStackThread(Action action, int stackBytes = ThreadStackBytes)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                captured = e;
            }
        }, stackBytes);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Test thread did not finish");
        return captured;
    }

    [Fact]
    public void NormalizeWhiteSpace_StringLongerThanStack_DoesNotOverflow()
    {
        // 1.2 МБ под stackalloc char[] при стеке 256 КБ.
        var big = new string('a', 300_000) + "  " + new string('b', 300_000);

        var error = RunOnSmallStackThread(() =>
        {
            var result = big.NormalizeWhiteSpace();
            Assert.Equal(big.Length - 1, result.Length);
            Assert.StartsWith("aaa", result, StringComparison.Ordinal);
        });

        Assert.Null(error);
    }

    [Fact]
    public void NormalizeWhiteSpace_HugeStringOnDefaultStack_DoesNotOverflow()
    {
        // 8 МБ под stackalloc char[] — переполняет и стек потока main по умолчанию.
        var huge = new string('x', 3_000_000) + " \t " + new string('y', 3_000_000);

        var result = huge.NormalizeWhiteSpace();

        Assert.Equal(huge.Length - 2, result.Length);
    }

    [Fact]
    public void NormalizeWhiteSpace_ShortString_StillNormalizes()
    {
        // Быстрый путь (stackalloc) не должен сломаться от перехода на кучу и обратно.
        Assert.Equal("hello world", "  hello   world  ".NormalizeWhiteSpace());
        // isTrimmed: false схлопывает внутренние пробелы, но края не срезает.
        Assert.Equal(" hello world ", "  hello   world  ".NormalizeWhiteSpace(isTrimmed: false));
    }

    [Fact]
    public void Levenshtein_Distance_StringsLongerThanStack_DoNotOverflow()
    {
        // 6 000 символов уводят расчёт на кучу (граница MaxStackallocInts = 1024), а наивный
        // stackalloc трёх полос занял бы 3 * 6 001 * 4 = 72 КБ — больше 64 КБ стека потока.
        // Прежние 60 000 символов давали 3.6 млрд итераций DP: 21 с в Release и больше
        // 60 с в Debug, где собственный Join-таймаут теста валил его как «завис».
        var a = new string('a', 6_000);
        var b = new string('b', 6_000);

        var error = RunOnSmallStackThread(() =>
        {
            Assert.Equal(6_000, Levenshtein.Distance(a, b));
            Assert.Equal(0, Levenshtein.Distance(a, a));
        }, SmallStackBytesForQuadraticWork);

        Assert.Null(error);
    }

    [Fact]
    public void Levenshtein_Distance_HeapAndStackPathsAgree()
    {
        // Граница MaxStackallocInts = 1024: ответ обязан быть одинаковым по обе стороны,
        // иначе один из путей считает по другой таблице.
        for (var len = 1015; len <= 1035; len++)
        {
            var a = new string('a', len);
            var differsInOneChar = string.Concat(new string('a', len - 1), "b");
            Assert.Equal(1, Levenshtein.Distance(a, differsInOneChar));
            Assert.Equal(len, Levenshtein.Distance(a, new string('b', len)));
            Assert.Equal(0, Levenshtein.Distance(a, a));
        }
    }

    [Fact]
    public void Levenshtein_Distance_TranspositionStillWorksOnHeapPath()
    {
        // Damerau: транспозиция = 1 правка. Длинная строка уходит на кучу — проверяем,
        // что перестановка в общем коде считается там же.
        var a = new string('a', 2000) + "bc" + new string('z', 2000);
        var b = new string('a', 2000) + "cb" + new string('z', 2000);
        Assert.Equal(1, Levenshtein.Distance(a, b));
    }

    [Fact]
    public void GetMurmurHash3_StackAndHeapPathsAgree()
    {
        foreach (var str in new[] { "", "a", new string('a', 100), new string('a', 170), new string('a', 171), new string('a', 5_000) })
        {
            // 170 * 3 = 510 — последняя длина на stackalloc, 171 * 3 = 513 уже на куче.
            var expected = MurmurHash3.Hash32(System.Text.Encoding.UTF8.GetBytes(str), 0);
            Assert.Equal(expected, str.GetMurmurHash3());
        }
    }

    [Fact]
    public void GetMurmurHash3_LongString_DoesNotOverflow()
    {
        var error = RunOnSmallStackThread(() =>
        {
            var hash = new string('a', 1_000_000).GetMurmurHash3();
            Assert.NotEqual(0u, hash);
        });

        Assert.Null(error);
    }
}
