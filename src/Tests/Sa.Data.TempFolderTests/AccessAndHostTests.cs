using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sa.Data.TempFolder;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Startup access validation and the background service: a broken root fails host start fast,
/// read-only instances are checked for readability only, and the periodic cleanup loop actually
/// removes expired folders on schedule.
/// </summary>
public sealed class AccessAndHostTests : IDisposable
{
    private readonly string _root = TestTemp.NewRoot();

    public void Dispose() => TestTemp.TryDelete(_root);

    private static ServiceProvider Build(Action<ITempFolderBuilder>? configure = null, Action<TempFolderOptions>? options = null)
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("access", builder =>
        {
            configure?.Invoke(builder);
            builder.Options(ob => ob.Configure(o => options?.Invoke(o)));
        });
        return services.BuildServiceProvider();
    }

    // ---------- CheckAccessAsync ----------

    [Fact]
    public async Task CheckAccess_ReadWrite_CreatesAMissingRoot()
    {
        var missing = TestTemp.NewMissingPath();

        try
        {
            using var provider = Build(options: o => o.RootPath = missing);
            var folder = provider.GetRequiredKeyedService<ITempFolder>("access");

            await folder.CheckAccessAsync(TestTemp.Token);

            Assert.True(Directory.Exists(missing));
        }
        finally
        {
            TestTemp.TryDelete(missing);
        }
    }

    [Fact]
    public async Task CheckAccess_ReadOnlyMissingRoot_Fails()
    {
        var missing = TestTemp.NewMissingPath();

        try
        {
            using var provider = Build(options: o =>
            {
                o.RootPath = missing;
                o.ReadOnly = true;
            });
            var folder = provider.GetRequiredKeyedService<ITempFolder>("access");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => folder.CheckAccessAsync(TestTemp.Token).AsTask());

            Assert.Contains("read-only", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(missing)); // RO never creates anything
        }
        finally
        {
            TestTemp.TryDelete(missing);
        }
    }

    [Fact]
    public async Task CheckAccess_ReadOnlyExistingRoot_PassesWithoutProbes()
    {
        using var provider = Build(options: o =>
        {
            o.RootPath = _root;
            o.ReadOnly = true;
        });
        var folder = provider.GetRequiredKeyedService<ITempFolder>("access");

        await folder.CheckAccessAsync(TestTemp.Token);

        Assert.Empty(Directory.GetFileSystemEntries(_root)); // no probe file/folder left behind
    }

    [Fact]
    public async Task CheckAccess_RootIsAFile_Fails()
    {
        var filePath = TestTemp.NewMissingPath();
        await File.WriteAllTextAsync(filePath, "i am a file", TestTemp.Token);

        try
        {
            using var provider = Build(options: o => o.RootPath = filePath);
            var folder = provider.GetRequiredKeyedService<ITempFolder>("access");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => folder.CheckAccessAsync(TestTemp.Token).AsTask());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task CheckAccess_LeavesNoProbeDebris()
    {
        using var provider = Build(options: o => o.RootPath = _root);
        var folder = provider.GetRequiredKeyedService<ITempFolder>("access");

        await folder.CheckAccessAsync(TestTemp.Token);

        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task ReadOnlyInstance_RejectsMutations_ButAllowsEnumeration()
    {
        File.WriteAllText(Path.Combine(_root, "existing.txt"), "data");

        using var provider = Build(options: o =>
        {
            o.RootPath = _root;
            o.ReadOnly = true;
        });
        var folder = provider.GetRequiredKeyedService<ITempFolder>("access");

        Assert.Throws<InvalidOperationException>(() => folder.CreateSubfolder());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.SaveStreamAsync(new MemoryStream(), "x.txt", TestTemp.Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.CopyFileAsync(Path.Combine(_root, "existing.txt"), null, TestTemp.Token).AsTask());

        var seen = 0;
        await foreach (var _ in folder.EnumerateFilesAsync("*.txt", cancellationToken: TestTemp.Token))
        {
            seen++;
        }

        Assert.Equal(1, seen);
    }

    // ---------- background host ----------

    [Fact]
    public async Task Host_StartFailsFast_WhenTheRootIsUnusable()
    {
        var filePath = TestTemp.NewMissingPath();
        await File.WriteAllTextAsync(filePath, "i am a file", TestTemp.Token);

        try
        {
            var services = new ServiceCollection();
            services.AddSaTempFolder("broken", b => b.Options(o => o.Configure(x => x.RootPath = filePath)));
            await using var provider = services.BuildServiceProvider();

            var host = provider.GetServices<IHostedService>().OfType<TempFolderCleanerHost>().Single();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(TestTemp.Token));

            Assert.Contains("cannot create root", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Host_PeriodicLoop_DeletesExpiredFoldersOnSchedule()
    {
        var expired = Path.Combine(_root, "expired");
        Directory.CreateDirectory(expired);
        TestTemp.MakeExpired(expired);

        var services = new ServiceCollection();
        services.AddSaTempFolder("loop", b => b.Options(o => o.Configure(x =>
        {
            x.RootPath = _root;
            x.CleanupInterval = TimeSpan.FromMilliseconds(200);
        })));
        await using var provider = services.BuildServiceProvider();

        var host = provider.GetServices<IHostedService>().OfType<TempFolderCleanerHost>().Single();
        await host.StartAsync(TestTemp.Token);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Directory.Exists(expired) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestTemp.Token);
            }

            Assert.False(Directory.Exists(expired), "the cleanup loop did not delete the expired folder in time");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Host_IgnoresReadOnlyInstances_WithoutFailingStartup()
    {
        var expired = Path.Combine(_root, "expired");
        Directory.CreateDirectory(expired);
        TestTemp.MakeExpired(expired);

        var services = new ServiceCollection();
        services.AddSaTempFolder("ro", b => b.Options(o => o.Configure(x =>
        {
            x.RootPath = _root;
            x.ReadOnly = true;
            x.CleanupInterval = TimeSpan.FromMilliseconds(100);
        })));
        await using var provider = services.BuildServiceProvider();

        var host = provider.GetServices<IHostedService>().OfType<TempFolderCleanerHost>().Single();
        await host.StartAsync(TestTemp.Token);

        try
        {
            await Task.Delay(500, TestTemp.Token);
            Assert.True(Directory.Exists(expired), "a read-only instance must never be cleaned");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
