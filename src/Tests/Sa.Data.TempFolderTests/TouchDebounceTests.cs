using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Debounced activation: a write refreshes the top-level folder's activity marker only after a
/// quiet period of <c>TouchDebounce</c>, and a burst of writes coalesces into one touch.
/// Virtual clock — no sleeping.
/// </summary>
public sealed class TouchDebounceTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = TestTemp.NewRoot();
    private readonly ManualTimeProvider _clock = new(Base);

    public void Dispose() => TestTemp.TryDelete(_root);

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Registered before AddSaTempFolder: its TryAddSingleton(TimeProvider.System) then
        // leaves the manual clock in place for the debouncer (and everything else).
        services.AddSingleton<TimeProvider>(_clock);

        services.AddSaTempFolder("debounce", b => b.Options(ob => ob.Configure(o => o.RootPath = _root)));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Write_DoesNotTouchImmediately_TouchesAfterTheDebounce()
    {
        using var provider = Build();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("debounce");

        // foo/sub pre-exists; foo's marker is backdated. Writing a file into foo/sub changes
        // foo/sub's own mtime (the file system does that itself) but leaves foo — the top-level
        // folder the cleanup ages — alone until the debounce fires.
        var foo = Path.Combine(_root, "foo");
        Directory.CreateDirectory(Path.Combine(foo, "sub"));
        var stale = Base.AddHours(-1).UtcDateTime;
        Directory.SetLastWriteTimeUtc(foo, stale);

        await folder.SaveStreamAsync(
            new MemoryStream("x"u8.ToArray()), Path.Combine("foo", "sub", "f.txt"), TestTemp.Token);

        Assert.Equal(stale, Directory.GetLastWriteTimeUtc(foo)); // no immediate touch

        _clock.Advance(TimeSpan.FromSeconds(5)); // TouchDebounce default

        Assert.Equal(Base.AddSeconds(5).UtcDateTime, Directory.GetLastWriteTimeUtc(foo));
    }

    [Fact]
    public async Task BurstOfWrites_CoalescesIntoASingleDelayedTouch()
    {
        using var provider = Build();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("debounce");

        var foo = Path.Combine(_root, "foo");
        Directory.CreateDirectory(Path.Combine(foo, "sub"));
        var stale = Base.AddHours(-1).UtcDateTime;
        Directory.SetLastWriteTimeUtc(foo, stale);

        // Write #1 arms the timer for t=5s.
        await folder.SaveStreamAsync(
            new MemoryStream("x"u8.ToArray()), Path.Combine("foo", "sub", "a.txt"), TestTemp.Token);

        _clock.Advance(TimeSpan.FromSeconds(2));

        // Write #2 at t=2s re-arms: the touch moves to t=7s.
        await folder.SaveStreamAsync(
            new MemoryStream("x"u8.ToArray()), Path.Combine("foo", "sub", "b.txt"), TestTemp.Token);

        _clock.Advance(TimeSpan.FromSeconds(4)); // t=6s — still inside the window

        Assert.Equal(stale, Directory.GetLastWriteTimeUtc(foo)); // coalesced, not touched at t=5s

        _clock.Advance(TimeSpan.FromSeconds(1)); // t=7s — the re-armed timer fires

        Assert.Equal(Base.AddSeconds(7).UtcDateTime, Directory.GetLastWriteTimeUtc(foo));
    }

    [Fact]
    public async Task Write_TouchesTheFileDirectoryAndEveryAncestor()
    {
        using var provider = Build();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("debounce");

        // The nested-cleanup contract: after the debounce, the written file's own directory and
        // every ancestor up to the root carry the fresh marker — the levels a nested age
        // criterion judges. Overwrites alone never move a directory's mtime, so the chain touch
        // is what keeps an active hierarchy alive at every level.
        var a = Path.Combine(_root, "a");
        Directory.CreateDirectory(Path.Combine(a, "b", "c"));
        var stale = Base.AddHours(-1).UtcDateTime;
        Directory.SetLastWriteTimeUtc(a, stale);
        Directory.SetLastWriteTimeUtc(Path.Combine(a, "b"), stale);

        await folder.SaveStreamAsync(
            new MemoryStream("x"u8.ToArray()), Path.Combine("a", "b", "c", "f.txt"), TestTemp.Token);

        // Немедленного касания нет ни на одном уровне.
        Assert.Equal(stale, Directory.GetLastWriteTimeUtc(a));
        Assert.Equal(stale, Directory.GetLastWriteTimeUtc(Path.Combine(a, "b")));

        _clock.Advance(TimeSpan.FromSeconds(5));

        var expected = Base.AddSeconds(5).UtcDateTime;
        Assert.Equal(expected, Directory.GetLastWriteTimeUtc(Path.Combine(a, "b", "c")));
        Assert.Equal(expected, Directory.GetLastWriteTimeUtc(Path.Combine(a, "b")));
        Assert.Equal(expected, Directory.GetLastWriteTimeUtc(a));
        Assert.NotEqual(expected, Directory.GetLastWriteTimeUtc(_root)); // корень не трогаем
    }

    [Fact]
    public async Task FileWrittenDirectlyInTheRoot_NeedsNoTopLevelTouch()
    {
        using var provider = Build();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("debounce");

        await folder.SaveStreamAsync(new MemoryStream("x"u8.ToArray()), "loose.txt", TestTemp.Token);

        // No top-level folder is involved; nothing to schedule — the clock has no armed timers
        // beyond what the write itself created (none).
        Assert.Equal(0, _clock.ArmedCount);
        Assert.True(File.Exists(Path.Combine(_root, "loose.txt")));
    }

    [Fact]
    public async Task DisposedInstance_DropsPendingTouches()
    {
        var provider = Build();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("debounce");

        var foo = Path.Combine(_root, "foo");
        Directory.CreateDirectory(Path.Combine(foo, "sub"));
        var stale = Base.AddHours(-1).UtcDateTime;
        Directory.SetLastWriteTimeUtc(foo, stale);

        await folder.SaveStreamAsync(
            new MemoryStream("x"u8.ToArray()), Path.Combine("foo", "sub", "f.txt"), TestTemp.Token);

        folder.Dispose(); // pending touch is cancelled — safe direction: the folder only stays older

        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(stale, Directory.GetLastWriteTimeUtc(foo));
        provider.Dispose();
    }
}
