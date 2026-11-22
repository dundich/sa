using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Data.TempFolder;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Volume tracking: the low-priority scan measures the root, the OnVolumeExceeded delegate fires
/// edge-triggered (once over, once back under), and with TrackVolume off nothing is ever measured.
/// </summary>
public sealed class VolumeTests : IDisposable
{
    private const string Key = "volume";
    private readonly string _root = TestTemp.NewRoot();

    public void Dispose() => TestTemp.TryDelete(_root);

    private ServiceProvider Build(Action<ITempFolderBuilder>? configure = null, Action<TempFolderOptions>? options = null)
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder(Key, builder =>
        {
            configure?.Invoke(builder);
            builder.Options(ob => ob.Configure(o =>
            {
                o.RootPath = _root;
                options?.Invoke(o);
            }));
        });
        return services.BuildServiceProvider();
    }

    private ITempFolder Resolve(ServiceProvider provider)
        => provider.GetRequiredKeyedService<ITempFolder>(Key);

    /// <summary>Creates a fresh (never expired) folder holding one file of the given size.</summary>
    private string MakeSizedFolder(string name, int bytes)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "payload.bin"), new byte[bytes]);
        return dir;
    }

    [Fact]
    public async Task ExceededLimit_FiresOnce_DoesNotRepeatWhileTheStateHolds()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(
            options: o =>
            {
                o.TrackVolume = true;
                o.MaxTotalSize = 10; // the payload below is 100 bytes
                o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
            });
        var folder = Resolve(provider);

        var dir = MakeSizedFolder("big", bytes: 100);
        Assert.True(Directory.Exists(dir));

        await folder.CleanupExpiredAsync(TestTemp.Token); // fresh folders survive; the scan runs first

        TempFolderVolumeArgs[] afterFirst;
        lock (events) afterFirst = events.ToArray();

        var over = Assert.Single(afterFirst);
        Assert.True(over.Exceeded);
        Assert.Equal(100, over.TotalSize);
        Assert.Equal(10, over.MaxTotalSize);
        Assert.Equal(Key, over.Name);

        // Second pass: same state → no repeated notification (edge-triggered).
        await folder.CleanupExpiredAsync(TestTemp.Token);

        lock (events) Assert.Single(events);
    }

    [Fact]
    public async Task FallingBackUnderTheLimit_FiresTheRecoveryEdge()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 10;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        var dir = MakeSizedFolder("big", bytes: 100);
        var cachedStamp = Directory.GetLastWriteTimeUtc(dir); // what the first scan keys the cache by

        await folder.CleanupExpiredAsync(TestTemp.Token);
        lock (events) Assert.Single(events);

        // Drop under the limit. The cache is keyed by the folder's stamp, so the stamp is moved to
        // a value the cache cannot hold — no sleep, and no dependence on the filesystem's
        // directory-mtime granularity (a delete landing in the cached tick would leave it valid).
        File.Delete(Path.Combine(dir, "payload.bin"));
        Directory.SetLastWriteTimeUtc(dir, cachedStamp.AddSeconds(-1));

        await folder.CleanupExpiredAsync(TestTemp.Token);

        TempFolderVolumeArgs[] all;
        lock (events) all = events.ToArray();

        Assert.Equal(2, all.Length);
        Assert.True(all[0].Exceeded);
        Assert.False(all[1].Exceeded);
        Assert.Equal(0, all[1].TotalSize);
    }

    [Fact]
    public async Task DeepDelete_InvalidatesTheCache_AndFiresTheRecoveryEdge()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 10;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        // A payload two levels down: deleting it changes only its own directory's stamp — the
        // top-level folder's stamp (the cache key) stays put, so without the explicit invalidation
        // on the delete path the next scan would keep serving 100 bytes and never recover.
        var deep = Path.Combine(_root, "2026", "10", "08");
        Directory.CreateDirectory(deep);
        await File.WriteAllBytesAsync(Path.Combine(deep, "payload.bin"), new byte[100], TestTemp.Token);

        await folder.CleanupExpiredAsync(TestTemp.Token);
        lock (events) Assert.Single(events); // over the limit — entry edge (fresh folders survive)

        Assert.True(await folder.DeleteFileAsync(Path.Combine("2026", "10", "08", "payload.bin"), TestTemp.Token));

        await folder.CleanupExpiredAsync(TestTemp.Token);

        TempFolderVolumeArgs[] all;
        lock (events) all = events.ToArray();

        Assert.Equal(2, all.Length);
        Assert.True(all[0].Exceeded);
        Assert.False(all[1].Exceeded);
        Assert.Equal(0, all[1].TotalSize);
    }

    [Fact]
    public async Task FirstScanUnderTheLimit_StaysSilent_AndTheNextCrossingStillFires()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 1000;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        // Starting already within the limit: no "recovery" has happened, so nothing is announced.
        MakeSizedFolder("small", bytes: 100);

        await folder.CleanupExpiredAsync(TestTemp.Token);
        await folder.CleanupExpiredAsync(TestTemp.Token);

        lock (events) Assert.Empty(events);

        // A later crossing is still a real edge: fires exactly once, no repeats.
        MakeSizedFolder("big", bytes: 2000);

        await folder.CleanupExpiredAsync(TestTemp.Token);
        await folder.CleanupExpiredAsync(TestTemp.Token);

        TempFolderVolumeArgs[] all;
        lock (events) all = events.ToArray();

        var fired = Assert.Single(all);
        Assert.True(fired.Exceeded);
        Assert.Equal(2100, fired.TotalSize);
    }

    [Fact]
    public async Task Scan_CancelledToken_ThrowsInsteadOfMeasuring()
    {
        using var scanner = new VolumeScanner("volume", _root, NullLogger.Instance);
        MakeSizedFolder("big", bytes: 100);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(new TempFolderOptions { RootPath = _root }, cts.Token));
    }

    [Fact]
    public async Task Cleanup_WithActivityInFlight_SkipsThePreCleanupScan()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 10;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        MakeSizedFolder("big", bytes: 100);
        await folder.WriteAsync(new MemoryStream(), "big/hold.bin", TestTemp.Token);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestTemp.Token;

        var read = folder.ReadAsync(
            "big/hold.bin",
            async (stream, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
            token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

        // While activity is in flight the pass cannot start, so the scan is skipped rather than
        // wasted — and no event is raised off a measurement nobody asked for.
        Assert.Equal(0, await folder.CleanupExpiredAsync(token));
        lock (events) Assert.Empty(events);

        release.SetResult();
        Assert.True(await read);

        // The next pass measures and reports the real total.
        await folder.CleanupExpiredAsync(token);
        lock (events) Assert.Single(events);
    }

    [Fact]
    public async Task Scan_PrunesTheCacheOfAVanishedFolder()
    {
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 1000;
        });
        var folder = (Sa.Data.TempFolder.TempFolder)Resolve(provider);

        var dir = MakeSizedFolder("big", bytes: 100);
        var stamp = Directory.GetLastWriteTimeUtc(dir);
        Assert.Equal(100, await folder.ScanVolumeAsync(TestTemp.Token));

        // Gone out-of-band: the next scan sees no folder and drops its entry.
        TestTemp.TryDelete(dir);
        Assert.Equal(0, await folder.ScanVolumeAsync(TestTemp.Token));

        // Recreated with the *same* stamp but different content — a surviving stale entry would
        // still match the key and serve 100.
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "payload.bin"), new byte[10], TestTemp.Token);
        Directory.SetLastWriteTimeUtc(dir, stamp);

        Assert.Equal(10, await folder.ScanVolumeAsync(TestTemp.Token));
    }

    [Fact]
    public async Task MaxTotalSizeTurnedOff_ResetsTheEdgeState()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 10;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = (Sa.Data.TempFolder.TempFolder)Resolve(provider);
        var options = folder.OptionsSnapshot;

        MakeSizedFolder("big", bytes: 100);
        Assert.Equal(100, await folder.ScanVolumeAsync(TestTemp.Token));
        lock (events) Assert.Single(events);

        // The limit is turned off: the monitor must forget the state instead of freezing it.
        options.MaxTotalSize = 0;
        Assert.Equal(100, await folder.ScanVolumeAsync(TestTemp.Token));
        lock (events) Assert.Single(events);

        // With the limit back, entering "over" is a fresh edge and is announced again.
        options.MaxTotalSize = 10;
        Assert.Equal(100, await folder.ScanVolumeAsync(TestTemp.Token));
        lock (events) Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task TrackVolumeOff_NeverMeasures_NeverFires()
    {
        var events = 0;
        using var provider = Build(options: o =>
        {
            o.MaxTotalSize = 1; // a limit that would trip immediately…
            o.TrackVolume = false; // …but measurement is off, so it is inert
            o.OnVolumeExceeded = _ => Interlocked.Increment(ref events);
        });
        var folder = Resolve(provider);

        MakeSizedFolder("big", bytes: 100);

        await folder.CleanupExpiredAsync(TestTemp.Token);
        await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(0, events);
    }

    [Fact]
    public async Task NoLimitConfigured_NeverFires()
    {
        var events = 0;
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 0; // no limit
            o.OnVolumeExceeded = _ => Interlocked.Increment(ref events);
        });
        var folder = Resolve(provider);

        MakeSizedFolder("big", bytes: 100);

        await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(0, events);
    }

    [Fact]
    public async Task HandlerExceptions_DoNotBreakThePass()
    {
        var measured = 0L;

        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 10;
            o.OnVolumeExceeded = e =>
            {
                measured = e.TotalSize;
                throw new InvalidOperationException("handler boom");
            };
        });
        var folder = Resolve(provider);

        var dir = MakeSizedFolder("big", bytes: 100);

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        // The scan really ran (the handler saw the measured total) and its exception was contained:
        // the fresh folder is kept and the pass is not lost.
        Assert.Equal(100, measured);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task Scan_MeasuresTheWholeRoot_IncludingLooseFiles()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 50;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        // 30 bytes in a subfolder + 30 loose bytes in the root = 60 > 50.
        MakeSizedFolder("part", bytes: 30);
        await File.WriteAllBytesAsync(Path.Combine(_root, "loose.bin"), new byte[30], TestTemp.Token);

        await folder.CleanupExpiredAsync(TestTemp.Token);

        TempFolderVolumeArgs[] over;
        lock (events) over = events.ToArray();

        var single = Assert.Single(over);
        Assert.Equal(60, single.TotalSize);
    }

    [Fact]
    public async Task CleanupDisabled_StillMeasuresTheVolume()
    {
        // The flag is about deletion only. With the default VolumeScanInterval = 0 the
        // pre-cleanup scan is volume tracking's only trigger, so a disabled cleanup must not
        // silence it.
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.CleanupEnabled = false;
            o.TrackVolume = true;
            o.MaxTotalSize = 10; // the payload below is 100 bytes
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        var dir = MakeSizedFolder("big", bytes: 100);

        Assert.Equal(0, await folder.CleanupExpiredAsync(TestTemp.Token)); // nothing is deleted
        Assert.True(Directory.Exists(dir));

        TempFolderVolumeArgs[] fired;
        lock (events) fired = events.ToArray();

        var single = Assert.Single(fired);
        Assert.True(single.Exceeded);
        Assert.Equal(100, single.TotalSize);
    }

    [Fact]
    public async Task OverwritingARootLevelFile_UpdatesTheMeasuredTotal()
    {
        var events = new List<TempFolderVolumeArgs>();
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 1000;
            o.OnVolumeExceeded = e => { lock (events) events.Add(e); };
        });
        var folder = Resolve(provider);

        // A file directly in the root is cached under the root's own stamp, and an overwrite moves
        // neither that stamp nor any ancestor's — the write must invalidate the entry explicitly,
        // or the total would stay frozen at the first measured size.
        await folder.WriteAsync(new MemoryStream(new byte[100]), "payload.bin", TestTemp.Token);
        await folder.CleanupExpiredAsync(TestTemp.Token); // first measurement: 100, under the limit

        TempFolderVolumeArgs[] before;
        lock (events) before = events.ToArray();
        Assert.Empty(before);

        await folder.WriteAsync(new MemoryStream(new byte[5000]), "payload.bin", TestTemp.Token);
        await folder.CleanupExpiredAsync(TestTemp.Token);

        TempFolderVolumeArgs[] fired;
        lock (events) fired = events.ToArray();

        var single = Assert.Single(fired);
        Assert.True(single.Exceeded);
        Assert.Equal(5000, single.TotalSize);
    }
}
