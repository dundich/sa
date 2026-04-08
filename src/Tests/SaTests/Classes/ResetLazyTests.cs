using Sa.Classes;
using System.Collections.Concurrent;

namespace SaTests.Classes;

public class ResetLazyTests
{
    [Fact]
    public void Value_ShouldBeInitializedLazy()
    {
        // Arrange
        int counter = 0;
        var lazy = new ResetLazy<int>(() => Interlocked.Increment(ref counter));

        // Assert
        Assert.Equal(0, counter);
        Assert.False(lazy.IsValueCreated);

        // Act
        var val = lazy.Value;

        // Assert
        Assert.Equal(1, val);
        Assert.Equal(1, counter);
        Assert.True(lazy.IsValueCreated);
    }

    [Fact]
    public void Reset_ShouldClearValue_AndCallCleanup()
    {
        // Arrange
        int counter = 0;
        int cleanupValue = -1;
        var lazy = new ResetLazy<List<int>>(
            () => [++counter],
            valueReset: list => cleanupValue = list[0]
        );

        // Act
        var firstList = lazy.Value;
        lazy.Reset();

        // Assert
        Assert.False(lazy.IsValueCreated);
        Assert.Equal(1, cleanupValue); // Cleanup вызван для первого списка

        var secondList = lazy.Value;
        Assert.Equal(2, secondList[0]); // Создан новый список
    }

    [Fact]
    public async Task MultithreadedAccess_ShouldCreateValueOnlyOnce()
    {
        // Arrange
        int factoryCalls = 0;
        var lazy = new ResetLazy<string>(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            Thread.Sleep(50); // Симуляция работы
            return Guid.NewGuid().ToString();
        });

        // Act
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => lazy.Value))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1, factoryCalls); // Фабрика вызвана ровно 1 раз
        Assert.All(results, r => Assert.Equal(results[0], r)); // Все потоки получили одну строку
    }

    [Fact]
    public async Task MultithreadedReset_ShouldBeSafe()
    {
        // Arrange
        var disposedItems = new ConcurrentBag<int>();
        int factoryCalls = 0;
        var lazy = new ResetLazy<int>(
            () => Interlocked.Increment(ref factoryCalls),
            valueReset: val => disposedItems.Add(val)
        );

        // Act
        // Интенсивно читаем и сбрасываем из разных потоков
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0) _ = lazy.Value;
            else lazy.Reset();
        }));

        await Task.WhenAll(tasks);

        // Assert
        // Проверяем, что сумма созданных объектов совпадает с суммой удаленных + 1 (если текущий остался)
        int currentExist = lazy.IsValueCreated ? 1 : 0;
        Assert.Equal(factoryCalls, disposedItems.Count + currentExist);
    }
}
