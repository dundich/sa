using Microsoft.Extensions.DependencyInjection;
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

        await folder.CleanupAsync(TestTemp.Token); // fresh folders survive; the scan runs first

        TempFolderVolumeArgs[] afterFirst;
        lock (events) afterFirst = events.ToArray();

        var over = Assert.Single(afterFirst);
        Assert.True(over.Exceeded);
        Assert.Equal(100, over.TotalSize);
        Assert.Equal(10, over.MaxTotalSize);
        Assert.Equal(Key, over.Name);

        // Second pass: same state → no repeated notification (edge-triggered).
        await folder.CleanupAsync(TestTemp.Token);

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

        await folder.CleanupAsync(TestTemp.Token);
        lock (events) Assert.Single(events);

        // Drop under the limit: deleting the payload changes the folder's last write time,
        // so the cache invalidates and the next scan sees the new total.
        // The delay matters: directory mtime has a tick granularity (ext4 ≈ up to 10 ms), so a
        // delete landing in the same tick as the cached stamp would leave the cache valid —
        // harmless in production (scans run minutes apart), fatal for a back-to-back test.
        await Task.Delay(50, TestTemp.Token);
        File.Delete(Path.Combine(dir, "payload.bin"));

        await folder.CleanupAsync(TestTemp.Token);

        TempFolderVolumeArgs[] all;
        lock (events) all = events.ToArray();

        Assert.Equal(2, all.Length);
        Assert.True(all[0].Exceeded);
        Assert.False(all[1].Exceeded);
        Assert.Equal(0, all[1].TotalSize);
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

        await folder.CleanupAsync(TestTemp.Token);
        await folder.CleanupAsync(TestTemp.Token);

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

        await folder.CleanupAsync(TestTemp.Token);

        Assert.Equal(0, events);
    }

    [Fact]
    public async Task HandlerExceptions_DoNotBreakThePass()
    {
        using var provider = Build(options: o =>
        {
            o.TrackVolume = true;
            o.MaxTotalSize = 10;
            o.OnVolumeExceeded = _ => throw new InvalidOperationException("handler boom");
        });
        var folder = Resolve(provider);

        MakeSizedFolder("big", bytes: 100);

        var removed = await folder.CleanupAsync(TestTemp.Token);

        Assert.Equal(0, removed); // fresh folder kept — the throwing handler did not kill the pass
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

        await folder.CleanupAsync(TestTemp.Token);

        TempFolderVolumeArgs[] over;
        lock (events) over = events.ToArray();

        var single = Assert.Single(over);
        Assert.Equal(60, single.TotalSize);
    }
}
