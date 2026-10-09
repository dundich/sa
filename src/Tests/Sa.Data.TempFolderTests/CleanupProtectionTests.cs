using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;
using Sa.Data.TempFolder.Cleanup;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Deletion yields to reads and writes: a pass never starts while Save/Copy/Download/Delete/CreateSubfolder
/// is in flight, an arriving read, write or delete cancels a running pass (the deletion ends by
/// cancellation), and a folder whose debounced marker touch has not landed yet is skipped — a
/// just-written file is never deleted by cleanup.
/// </summary>
public sealed class CleanupProtectionTests : IDisposable
{
    private const string Key = "protect";
    private static readonly DateTimeOffset Base = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = TestTemp.NewRoot();

    public void Dispose() => TestTemp.TryDelete(_root);

    private ServiceProvider Build(Action<ITempFolderBuilder>? configure = null, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();

        if (clock is not null)
        {
            // Registered before AddSaTempFolder: its TryAddSingleton(TimeProvider.System) then
            // leaves the manual clock in place for the debouncer, the strategy and the cleanup.
            services.AddSingleton<TimeProvider>(clock);
        }

        services.AddSaTempFolder(Key, builder =>
        {
            configure?.Invoke(builder);
            builder.Options(ob => ob.Configure(o => o.RootPath = _root));
        });
        return services.BuildServiceProvider();
    }

    private ITempFolder Resolve(ServiceProvider provider)
        => provider.GetRequiredKeyedService<ITempFolder>(Key);

    private string MakeSubfolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------- the gate itself ----------

    [Fact]
    public async Task CleanupGate_RefusesAPassWhileActivityIsInFlight()
    {
        var gate = new CleanupGate();

        using (await gate.EnterActivityAsync(TestTemp.Token))
        {
            Assert.Null(gate.TryEnterCleanup(TestTemp.Token));
        }

        // Activity gone → the next pass may start.
        using var pass = gate.TryEnterCleanup(TestTemp.Token);
        Assert.NotNull(pass);
    }

    [Fact]
    public async Task CleanupGate_AnArrivingWriteCancelsThePassAndWaitsForItToUnwind()
    {
        var gate = new CleanupGate();

        using var pass = gate.TryEnterCleanup(TestTemp.Token);
        Assert.NotNull(pass);

        var activity = gate.EnterActivityAsync(TestTemp.Token).AsTask();

        // The write cancelled the pass on the spot and waits only for it to unwind.
        Assert.False(activity.IsCompleted);
        Assert.True(pass!.Token.IsCancellationRequested);

        pass.Dispose();

        using (await activity.WaitAsync(TimeSpan.FromSeconds(10), TestTemp.Token))
        {
            // The write entered only after the pass released the gate — and now excludes a new one.
            Assert.Null(gate.TryEnterCleanup(TestTemp.Token));
        }

        using var next = gate.TryEnterCleanup(TestTemp.Token);
        Assert.NotNull(next);
    }

    // ---------- Save / Copy vs the pass ----------

    [Fact]
    public async Task Cleanup_YieldsWhileASaveIsInFlight()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var expired = MakeSubfolder("expired");
        TestTemp.MakeExpired(expired);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestTemp.Token;

        var save = folder.WriteAsync(
            new GatedStream("payload"u8.ToArray(), entered, release.Task), "expired/f.bin", token).AsTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

        // The write is in flight: the pass does not start at all — the folder is untouched.
        var removed = await folder.CleanupExpiredAsync(token);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(expired));

        release.SetResult();
        await save;

        Assert.True(File.Exists(Path.Combine(expired, "f.bin")));

        // Even after the write completed its marker touch is still pending — the next pass
        // skips the folder again rather than taking the fresh file.
        removed = await folder.CleanupExpiredAsync(token);
        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(expired, "f.bin")));
    }

    [Fact]
    public async Task CopyFileAsync_IsProtectedByTheSameExclusion()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var source = TestTemp.NewMissingPath() + ".bin";
        await File.WriteAllTextAsync(source, "payload", TestTemp.Token);

        var target = MakeSubfolder("c_old");
        TestTemp.MakeExpired(target);

        try
        {
            var (_, absolute) = await folder.CopyFileAsync(source, "c_old", TestTemp.Token);

            // The copy completed under the activity lease; its marker touch is pending, so the
            // pass skips the folder instead of taking the fresh file.
            var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

            Assert.Equal(0, removed);
            Assert.True(File.Exists(absolute));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task Download_HoldsTheActivityLease_CleanupYieldsAndTheFileSurvives()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var data = MakeSubfolder("data");
        var file = Path.Combine(data, "payload.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        TestTemp.MakeExpired(data); // expired with no pending touch — a legitimate candidate

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestTemp.Token;

        var download = folder.ReadAsync(
            Path.Combine("data", "payload.bin"),
            async (stream, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);

                var buffer = new byte[3];
                Assert.Equal(3, await stream.ReadAsync(buffer, ct));
                Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
            },
            token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

        // The read is in flight: the pass does not start at all — the folder is untouched.
        var removed = await folder.CleanupExpiredAsync(token);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(data));

        release.SetResult();
        Assert.True(await download);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task WriteArrivingMidPass_CancelsTheDeletion_AndTheFileSurvives()
    {
        BlockingStrategy.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingStrategy.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            using var provider = Build(configure: b => b.UseCleanupStrategy<BlockingStrategy>());
            var folder = Resolve(provider);
            var victim = MakeSubfolder("victim");
            var token = TestTemp.Token;

            var cleanup = Task.Run(async () => await folder.CleanupExpiredAsync(token), token);

            // The pass is now inside the selection — the gate is held.
            await BlockingStrategy.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

            // The write interrupts the pass and waits only for it to unwind.
            var save = folder.WriteAsync(
                new MemoryStream("x"u8.ToArray()), "victim/f.txt", token).AsTask();
            Assert.False(save.IsCompleted);

            BlockingStrategy.Release!.SetResult();

            var removed = await cleanup;
            await save;

            // The deletion ended by cancellation before touching anything; the write landed after.
            Assert.Equal(0, removed);
            Assert.True(Directory.Exists(victim));
            Assert.True(File.Exists(Path.Combine(victim, "f.txt")));
        }
        finally
        {
            BlockingStrategy.Release?.TrySetResult();
            BlockingStrategy.Entered = null;
            BlockingStrategy.Release = null;
        }
    }

    [Fact]
    public async Task ReadArrivingMidPass_CancelsTheDeletion_AndTheFileSurvives()
    {
        BlockingStrategy.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingStrategy.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            using var provider = Build(configure: b => b.UseCleanupStrategy<BlockingStrategy>());
            var folder = Resolve(provider);
            var victim = MakeSubfolder("victim");
            var file = Path.Combine(victim, "f.txt");
            File.WriteAllText(file, "payload");
            var token = TestTemp.Token;

            var cleanup = Task.Run(async () => await folder.CleanupExpiredAsync(token), token);

            // The pass is now inside the selection — the gate is held.
            await BlockingStrategy.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

            // The read interrupts the pass and waits only for it to unwind.
            var download = folder.ReadAsync(
                Path.Combine("victim", "f.txt"),
                (stream, ct) => stream.CopyToAsync(Stream.Null, ct),
                token);
            Assert.False(download.IsCompleted);

            BlockingStrategy.Release!.SetResult();

            var removed = await cleanup;
            Assert.True(await download);

            // The deletion ended by cancellation before touching anything; the read landed after.
            Assert.Equal(0, removed);
            Assert.True(Directory.Exists(victim));
            Assert.True(File.Exists(file));
        }
        finally
        {
            BlockingStrategy.Release?.TrySetResult();
            BlockingStrategy.Entered = null;
            BlockingStrategy.Release = null;
        }
    }

    [Fact]
    public async Task DeleteArrivingMidPass_CancelsTheDeletion_AndTheFileIsRemoved()
    {
        BlockingStrategy.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingStrategy.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            using var provider = Build(configure: b => b.UseCleanupStrategy<BlockingStrategy>());
            var folder = Resolve(provider);
            var victim = MakeSubfolder("victim");
            var file = Path.Combine(victim, "f.txt");
            File.WriteAllText(file, "payload");
            var token = TestTemp.Token;

            var cleanup = Task.Run(async () => await folder.CleanupExpiredAsync(token), token);

            // The pass is now inside the selection — the gate is held.
            await BlockingStrategy.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

            // The delete interrupts the pass and waits only for it to unwind.
            var delete = folder.DeleteFileAsync(Path.Combine("victim", "f.txt"), token);
            Assert.False(delete.IsCompleted);

            BlockingStrategy.Release!.SetResult();

            var removed = await cleanup;
            Assert.True(await delete);

            // The deletion ended by cancellation before touching anything; the file is gone,
            // its folder (an empty directory) is not — delete only takes files.
            Assert.Equal(0, removed);
            Assert.False(File.Exists(file));
            Assert.True(Directory.Exists(victim));
        }
        finally
        {
            BlockingStrategy.Release?.TrySetResult();
            BlockingStrategy.Entered = null;
            BlockingStrategy.Release = null;
        }
    }

    [Fact]
    public async Task Delete_RunsSideBySideWithAnOpenRead_AndCleanupYieldsToBoth()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var reader = MakeSubfolder("reader");
        var readTarget = Path.Combine(reader, "payload.bin");
        File.WriteAllBytes(readTarget, [1, 2, 3]);
        TestTemp.MakeExpired(reader);

        var victim = MakeSubfolder("victim");
        var deleteTarget = Path.Combine(victim, "f.txt");
        File.WriteAllText(deleteTarget, "payload");
        TestTemp.MakeExpired(victim);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestTemp.Token;

        var download = folder.ReadAsync(
            Path.Combine("reader", "payload.bin"),
            async (stream, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
            token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

        // Reads and deletes share the activity side — the delete completes while the read is
        // still open instead of queueing behind it.
        Assert.True(await folder.DeleteFileAsync(Path.Combine("victim", "f.txt"), token)
            .WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.False(File.Exists(deleteTarget));

        // The read lease is still held: neither expired folder is a legitimate candidate now.
        var removed = await folder.CleanupExpiredAsync(token);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(reader));
        Assert.True(Directory.Exists(victim));

        release.SetResult();
        Assert.True(await download);
    }

    [Fact]
    public async Task Cleanup_SkipsAFolderWhoseMarkerTouchIsStillPending()
    {
        var clock = new ManualTimeProvider(Base);
        using var provider = Build(clock: clock);
        var folder = Resolve(provider);

        var busy = Path.Combine(_root, "busy");
        Directory.CreateDirectory(busy);
        // Expired by the virtual clock (and the real FS timestamp keeps it expired regardless).
        Directory.SetLastWriteTimeUtc(busy, clock.GetUtcNow().UtcDateTime.AddHours(-48));

        await folder.WriteAsync(new MemoryStream("x"u8.ToArray()), "busy/f.txt", TestTemp.Token);

        // The write just happened; its debounce touch is armed but has not fired — the pass
        // must not take the folder with the fresh file inside.
        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(busy));

        clock.Advance(TimeSpan.FromSeconds(5)); // TouchDebounce default: the walk lands

        removed = await folder.CleanupExpiredAsync(TestTemp.Token);
        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(busy)); // marker refreshed → no longer expired

        // Expire it again with no pending touch — now the pass may take it.
        Directory.SetLastWriteTimeUtc(busy, clock.GetUtcNow().UtcDateTime.AddHours(-48));
        removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(busy));
    }

    /// <summary>
    /// Blocks the reading side until released — holds the save (and therefore its activity
    /// lease) at a precise point instead of racing on timing.
    /// </summary>
    private sealed class GatedStream(byte[] data, TaskCompletionSource entered, Task release) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == 0 && !entered.Task.IsCompleted)
            {
                entered.TrySetResult();
                await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var read = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, read).CopyTo(buffer.Span);
            _position += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Cleanup strategy that blocks inside the pass — the test uses it to open the window in
    /// which a write arrives mid-pass and must cancel the deletion.
    /// </summary>
    private sealed class BlockingStrategy : ICleanupStrategy
    {
        /// <summary>Set by the test: signals that the pass is running (gate held).</summary>
        public static TaskCompletionSource? Entered { get; set; }

        /// <summary>Set by the test: released to let the pass continue.</summary>
        public static TaskCompletionSource? Release { get; set; }

        public IReadOnlyList<string> SelectForDeletion(CleanupContext context)
        {
            Entered!.TrySetResult();
            Release!.Task.GetAwaiter().GetResult();
            return [Path.Combine(context.RootPath, "victim")];
        }
    }
}
