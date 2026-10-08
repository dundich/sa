using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sa.Data.TempFolder;

// Inside Sa.Data.TempFolderTests the simple name "TempFolder" binds to the sibling *namespace*
// Sa.Data.TempFolder before any using comes into play — the implementation class needs an alias.
using TempFolderImpl = Sa.Data.TempFolder.TempFolder;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Free-space watching: the monitor fires its callback edge-triggered on reaching the limit
/// (and on recovery), never while the state holds, survives a throwing handler, propagates probe
/// failures to the loop — and the background service schedules the check only when a limit is set.
/// </summary>
public sealed class FreeSpaceTests : IDisposable
{
    private readonly string _root = TestTemp.NewRoot();

    public void Dispose() => TestTemp.TryDelete(_root);

    private TempFolderOptions Options(long minFreeSpace)
        => new() { RootPath = _root, MinFreeSpace = minFreeSpace };

    private ServiceProvider Build(Action<TempFolderOptions>? options = null)
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("fs", b => b.Options(ob => ob.Configure(o =>
        {
            o.RootPath = _root;
            options?.Invoke(o);
        })));
        return services.BuildServiceProvider();
    }

    // ---------- FreeSpaceMonitor ----------

    [Fact]
    public void Check_FiresEdgeTriggered_WhenTheLimitIsReached()
    {
        var available = 100L;
        var monitor = new FreeSpaceMonitor("fs", _root, NullLogger.Instance, _ => available);
        var events = new List<TempFolderFreeSpaceArgs>();
        var options = Options(minFreeSpace: 50);
        options.OnFreeSpaceReached = events.Add;

        Assert.Equal(100, monitor.Check(options)); // выше лимита — тишина
        Assert.Empty(events);

        available = 50; // ровно на лимите — достигнут
        monitor.Check(options);
        var first = Assert.Single(events);
        Assert.True(first.Reached);
        Assert.Equal(50, first.AvailableFreeSpace);
        Assert.Equal(50, first.MinFreeSpace);
        Assert.Equal("fs", first.Name);
        Assert.Equal(_root, first.RootPath);

        available = 40; // состояние держится — без повтора
        monitor.Check(options);
        Assert.Single(events);

        available = 60; // восстановление — второй переход
        monitor.Check(options);
        Assert.Equal(2, events.Count);
        Assert.False(events[1].Reached);

        available = 10; // новое падение — снова «достигнут»
        monitor.Check(options);
        Assert.Equal(3, events.Count);
        Assert.True(events[2].Reached);
    }

    [Fact]
    public void Check_WithTheLimitOff_NeverFires_AndForgetsTheState()
    {
        var available = 10L;
        var monitor = new FreeSpaceMonitor("fs", _root, NullLogger.Instance, _ => available);
        var events = new List<TempFolderFreeSpaceArgs>();
        var options = Options(minFreeSpace: 50);
        options.OnFreeSpaceReached = events.Add;

        monitor.Check(options);
        Assert.Single(events); // лимит был достигнут

        options.MinFreeSpace = 0; // выключили — проверка не стреляет и забывает состояние
        monitor.Check(options);
        Assert.Single(events);

        options.MinFreeSpace = 50; // включили снова — состояние пересчитано с нуля
        monitor.Check(options);
        Assert.Equal(2, events.Count);
        Assert.True(events[1].Reached);
    }

    [Fact]
    public void Check_HandlerThrows_IsCaught()
    {
        var available = 1L;
        var monitor = new FreeSpaceMonitor("fs", _root, NullLogger.Instance, _ => available);
        var options = Options(minFreeSpace: 50);
        options.OnFreeSpaceReached = _ => throw new InvalidOperationException("handler boom");

        // Оба перехода проходят через кидающий обработчик — исключение не выходит наружу.
        monitor.Check(options);
        available = 100;
        monitor.Check(options);

        // Состояние держится — обработчик даже не вызывается.
        Assert.Equal(100, monitor.Check(options));
    }

    [Fact]
    public void Check_ReaderFails_PropagatesForTheLoopToLog()
    {
        var monitor = new FreeSpaceMonitor(
            "fs", _root, NullLogger.Instance, _ => throw new IOException("volume gone"));

        Assert.Throws<IOException>(() => monitor.Check(Options(minFreeSpace: 50)));
    }

    [Fact]
    public void Check_UsesDriveInfoByDefault()
    {
        // Реальный вольюм корня без фейкового ридера: штатный путь DriveInfo обязан работать
        // на текущей платформе и вернуть положительный остаток.
        var monitor = new FreeSpaceMonitor("fs", _root, NullLogger.Instance);

        var available = monitor.Check(Options(minFreeSpace: 0));

        Assert.True(available > 0, $"expected positive free space, but got {available}");
    }

    // ---------- options validation ----------

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Register_NegativeMinFreeSpace_FailsValidation(long minFreeSpace)
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("neg", b => b.Options(o => o.Configure(x => x.MinFreeSpace = minFreeSpace)));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>("neg"));

        Assert.Contains("MinFreeSpace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_NonPositiveFreeSpaceCheckInterval_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("noint",
            b => b.Options(o => o.Configure(x => x.FreeSpaceCheckInterval = TimeSpan.Zero)));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>("noint"));

        Assert.Contains("FreeSpaceCheckInterval", ex.Message, StringComparison.Ordinal);
    }

    // ---------- background wiring ----------

    [Fact]
    public void FreeSpaceCheck_IsGatedOnAMinFreeSpaceLimit()
    {
        using var off = Build(); // по умолчанию лимита нет
        var offFolder = (TempFolderImpl)off.GetRequiredKeyedService<ITempFolder>("fs");
        Assert.False(offFolder.HasFreeSpaceCheck());

        using var on = Build(o => o.MinFreeSpace = 1024);
        var onFolder = (TempFolderImpl)on.GetRequiredKeyedService<ITempFolder>("fs");
        Assert.True(onFolder.HasFreeSpaceCheck());
    }

    [Fact]
    public async Task Host_FreeSpaceLoop_ChecksAtStartupAndFiresTheCallback()
    {
        var events = new List<TempFolderFreeSpaceArgs>();

        using var provider = Build(o =>
        {
            // Любого реального объёма меньше — лимит достигается на первом же опросе.
            o.MinFreeSpace = long.MaxValue;
            o.OnFreeSpaceReached = events.Add;
        });

        var host = provider.GetServices<IHostedService>().OfType<TempFolderCleanerHost>().Single();
        await host.StartAsync(TestTemp.Token);

        try
        {
            // ExecuteAsync стартует вскоре за StartAsync — ждём первый (немедленный) проход.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (events.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestTemp.Token);
            }

            var fired = Assert.Single(events);
            Assert.True(fired.Reached);
            Assert.Equal(long.MaxValue, fired.MinFreeSpace);
            Assert.True(fired.AvailableFreeSpace > 0, $"expected positive free space, got {fired.AvailableFreeSpace}");
            Assert.Equal(Path.GetFullPath(_root), fired.RootPath);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
