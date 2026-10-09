using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// The public <see cref="ITempFolder"/> surface: strategy-named subfolders, stream/file writes,
/// pattern enumeration — and the path guard applied at every entry point.
/// </summary>
public sealed class TempFolderApiTests : IDisposable
{
    private const string Key = "api";
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

    private static ITempFolder Resolve(ServiceProvider provider)
        => provider.GetRequiredKeyedService<ITempFolder>(Key);

    // ---------- CreateSubfolder ----------

    [Fact]
    public void CreateSubfolder_WithoutArguments_GeneratesPrefixPlusGuidV7()
    {
        using var provider = Build(options: o => o.FolderPrefix = "tfp_");
        var folder = Resolve(provider);

        var created = folder.CreateSubfolder();

        var name = Path.GetFileName(created);
        Assert.StartsWith("tfp_", name, StringComparison.Ordinal);

        var guid = name["tfp_".Length..];
        Assert.True(Guid.TryParseExact(guid, "N", out var parsed), $"'{guid}' is not a 32-hex Guid.");

        // RFC 4122 version nibble sits at index 12 of the dash-less "N" form; v7 → '7'.
        Assert.Equal('7', guid[12]);
        Assert.True(Directory.Exists(created));
    }

    [Fact]
    public void CreateSubfolder_CallerChosenPath_CreatesParents()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var created = folder.CreateSubfolder(Path.Combine("jobs", "42"));

        Assert.Equal(Path.Combine(_root, "jobs", "42"), created);
        Assert.True(Directory.Exists(created));
    }

    [Fact]
    public void CreateSubfolder_EscapingTheRoot_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        Assert.Throws<SecurityException>(() => folder.CreateSubfolder(".."));
    }

    [Fact]
    public void CreateSubfolder_WithInjectionCharacter_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        // No shell expansion happens here — "~"/">" in a subfolder name is an injection attempt.
        Assert.Throws<SecurityException>(() => folder.CreateSubfolder(Path.Combine("jo>bs", "42")));
        Assert.Throws<SecurityException>(() => folder.CreateSubfolder("~hostile"));
    }

    // ---------- WriteAsync ----------

    [Fact]
    public async Task SaveStream_CreatesParents_AndReturnsBothPaths()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        var payload = "hello temp"u8.ToArray();

        await using var source = new MemoryStream(payload);
        var (relative, absolute) = await folder.WriteAsync(
            source, Path.Combine("a", "b", "note.txt"), TestTemp.Token);

        Assert.Equal(Path.Combine("a", "b", "note.txt"), relative);
        Assert.Equal(Path.Combine(_root, "a", "b", "note.txt"), absolute);
        Assert.True(Path.IsPathRooted(absolute));
        Assert.Equal(payload, await File.ReadAllBytesAsync(absolute, TestTemp.Token));
    }

    [Fact]
    public async Task SaveStream_EscapingTheRoot_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.WriteAsync(source, Path.Combine("..", "escape.txt"), TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_AbsolutePathInsideRoot_Writes()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        var absoluteTarget = Path.Combine(Path.GetFullPath(_root), "abs", "deep", "file.txt");

        await using var source = new MemoryStream("abs"u8.ToArray());
        var (relative, absolute) = await folder.WriteAsync(source, absoluteTarget, TestTemp.Token);

        Assert.Equal(Path.Combine("abs", "deep", "file.txt"), relative);
        Assert.Equal(absoluteTarget, absolute);
        Assert.Equal("abs", await File.ReadAllTextAsync(absolute, TestTemp.Token));
    }

    [Fact]
    public async Task SaveStream_AbsolutePathOutsideRoot_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        var outside = Path.Combine(Path.GetTempPath(), "sa_tf_outside_" + Path.GetRandomFileName(), "evil.txt");

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.WriteAsync(source, outside, TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_WithInjectionCharacter_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.WriteAsync(source, "data/we~ird.txt", TestTemp.Token).AsTask());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.WriteAsync(source, "data/re>direct.txt", TestTemp.Token).AsTask());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.WriteAsync(source, "data/a/../b.txt", TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_EmptyPath_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(
            () => folder.WriteAsync(source, "  ", TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_PointingAtADirectory_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        folder.CreateSubfolder("adir");

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.WriteAsync(source, "adir", TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_PreallocatesTheTargetAtOpen()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestTemp.Token;
        var target = Path.Combine(_root, "pre", "big.bin");

        var save = folder.WriteAsync(
            new GatedSeekableStream(payload, entered, release.Task), "pre/big.bin", token).AsTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

        // The source has not handed over a single byte yet (it blocks on its first read), so any
        // reservation came from the preallocation at open. The platforms observe it differently:
        // Windows extends the file length, Unix keeps i_size at 0 and reserves blocks instead
        // (FALLOC_FL_KEEP_SIZE) — blocks are visible via stat's %b (512-byte units).
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(payload.Length, new FileInfo(target).Length);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.True(
                GetAllocatedBytes(target) >= payload.Length,
                $"target reserved only {GetAllocatedBytes(target)} of {payload.Length} bytes");
        }

        release.SetResult();
        await save;
        Assert.Equal(payload, await File.ReadAllBytesAsync(target, token));
    }

    [Fact]
    public async Task SaveStream_AdvancedSource_WritesOnlyTheRemainingBytes()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);

        await using var source = new MemoryStream(payload);
        source.Position = 600; // the copy starts mid-stream: preallocate the remainder, not the whole

        var (_, absolute) = await folder.WriteAsync(source, "part/f.bin", TestTemp.Token);

        Assert.Equal(400, new FileInfo(absolute).Length); // no zero padding at the tail
        Assert.Equal(payload.AsSpan(600).ToArray(), await File.ReadAllBytesAsync(absolute, TestTemp.Token));
    }

    private static long GetAllocatedBytes(string path)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("stat")
        {
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("%b");
        startInfo.ArgumentList.Add(path);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var blocks = long.Parse(
            process.StandardOutput.ReadToEnd().Trim(), System.Globalization.CultureInfo.InvariantCulture);
        process.WaitForExit();
        return blocks * 512;
    }

    // ---------- OverwriteFiles ----------

    [Fact]
    public async Task SaveStream_OverwritesAnExistingFileByDefault()
    {
        using var provider = Build(); // OverwriteFiles по умолчанию = true
        var folder = Resolve(provider);

        await folder.WriteAsync(new MemoryStream("first"u8.ToArray()), "x/f.txt", TestTemp.Token);
        await folder.WriteAsync(new MemoryStream("second"u8.ToArray()), "x/f.txt", TestTemp.Token);

        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(_root, "x", "f.txt"), TestTemp.Token));
    }

    [Fact]
    public async Task SaveStream_NoOverwrite_ThrowsAndKeepsTheOriginal()
    {
        using var provider = Build(options: o => o.OverwriteFiles = false);
        var folder = Resolve(provider);
        var target = Path.Combine(_root, "x", "f.txt");

        await folder.WriteAsync(new MemoryStream("first"u8.ToArray()), "x/f.txt", TestTemp.Token);

        await using var again = new MemoryStream("second"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(
            () => folder.WriteAsync(again, "x/f.txt", TestTemp.Token).AsTask());

        Assert.Equal("first", await File.ReadAllTextAsync(target, TestTemp.Token));
    }

    [Fact]
    public async Task CopyFile_NoOverwrite_ThrowsAndKeepsTheOriginal()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "sa_tf_src_" + Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(sourcePath, [1, 2], TestTemp.Token);

        // CopyFile keeps the source file's name — the clash must be staged under that name.
        var existing = Path.Combine(_root, "inbox", Path.GetFileName(sourcePath));
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllBytesAsync(existing, [9], TestTemp.Token);

        try
        {
            using var provider = Build(options: o => o.OverwriteFiles = false);
            var folder = Resolve(provider);

            await Assert.ThrowsAsync<IOException>(
                () => folder.CopyFileAsync(sourcePath, "inbox", TestTemp.Token).AsTask());

            Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(existing, TestTemp.Token));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    // ---------- CopyFileAsync ----------

    [Fact]
    public async Task CopyFile_IntoChosenSubfolder_KeepsTheFileName()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "sa_tf_src_" + Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3], TestTemp.Token);

        try
        {
            using var provider = Build();
            var folder = Resolve(provider);

            var (relative, absolute) = await folder.CopyFileAsync(sourcePath, "inbox", TestTemp.Token);

            Assert.Equal(Path.Combine("inbox", Path.GetFileName(sourcePath)), relative);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(absolute, TestTemp.Token));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task CopyFile_AbsoluteSubfolderInsideRoot_Writes()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "sa_tf_src_" + Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(sourcePath, [4, 5], TestTemp.Token);

        try
        {
            using var provider = Build();
            var folder = Resolve(provider);
            var absoluteDir = Path.Combine(Path.GetFullPath(_root), "abs", "inbox");

            var (relative, absolute) = await folder.CopyFileAsync(sourcePath, absoluteDir, TestTemp.Token);

            // The absolute argument is accepted like a relative one — the returned relative path
            // stays rooted at the instance root.
            Assert.Equal(Path.Combine("abs", "inbox", Path.GetFileName(sourcePath)), relative);
            Assert.Equal(Path.Combine(absoluteDir, Path.GetFileName(sourcePath)), absolute);
            Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(absolute, TestTemp.Token));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task CopyFile_AbsoluteSubfolderOutsideRoot_IsRejected()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "sa_tf_src_" + Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(sourcePath, [4], TestTemp.Token);
        var outside = Path.Combine(Path.GetTempPath(), "sa_tf_outside_" + Path.GetRandomFileName());

        try
        {
            using var provider = Build();
            var folder = Resolve(provider);

            await Assert.ThrowsAsync<SecurityException>(
                () => folder.CopyFileAsync(sourcePath, outside, TestTemp.Token).AsTask());
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task CopyFile_WithoutSubfolder_BuildsAStrategyNamedOne()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "sa_tf_src_" + Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(sourcePath, [7], TestTemp.Token);

        try
        {
            using var provider = Build(options: o => o.FolderPrefix = "cp_");
            var folder = Resolve(provider);

            var (relative, absolute) = await folder.CopyFileAsync(sourcePath, null, TestTemp.Token);

            var parent = Path.GetDirectoryName(absolute)!;
            Assert.NotEqual(_root, parent); // a fresh subfolder was built for the copy
            Assert.StartsWith("cp_", Path.GetFileName(parent), StringComparison.Ordinal);
            Assert.Equal(Path.GetFileName(sourcePath), Path.GetFileName(absolute));
            Assert.False(Path.IsPathRooted(relative)); // relative to the root
            Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(absolute, TestTemp.Token));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task CopyFile_MissingSource_ThrowsFileNotFound()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => folder.CopyFileAsync(Path.Combine(_root, "nope.bin"), null, TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task CopyFile_SourceNameWithInjectionCharacter_IsRejected()
    {
        // The source may live anywhere on disk, but its name would be reused inside the temp
        // folder — "weird~name.bin" is rejected before anything is created.
        var sourcePath = Path.Combine(Path.GetTempPath(), "sa_tf_weird~name_" + Path.GetRandomFileName() + ".bin");
        await File.WriteAllBytesAsync(sourcePath, [9], TestTemp.Token);

        try
        {
            using var provider = Build();
            var folder = Resolve(provider);

            await Assert.ThrowsAsync<SecurityException>(
                () => folder.CopyFileAsync(sourcePath, null, TestTemp.Token).AsTask());
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    // ---------- ReadAsync ----------

    [Fact]
    public async Task Download_RelativePath_ReadsTheFile()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await folder.WriteAsync(
            new MemoryStream("payload"u8.ToArray()), Path.Combine("d", "f.bin"), TestTemp.Token);

        byte[]? got = null;
        var found = await folder.ReadAsync(
            Path.Combine("d", "f.bin"),
            async (stream, ct) =>
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                got = buffer.ToArray();
            },
            TestTemp.Token);

        Assert.True(found);
        Assert.Equal("payload"u8.ToArray(), got);
    }

    [Fact]
    public async Task Download_AbsolutePathInsideRoot_ReadsTheFile()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var (_, absolute) = await folder.WriteAsync(
            new MemoryStream("abs"u8.ToArray()), Path.Combine("abs", "deep", "file.txt"), TestTemp.Token);

        byte[]? got = null;
        var found = await folder.ReadAsync(
            absolute,
            async (stream, ct) =>
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                got = buffer.ToArray();
            },
            TestTemp.Token);

        Assert.True(found);
        Assert.Equal("abs"u8.ToArray(), got);
    }

    [Fact]
    public async Task Download_AbsolutePathOutsideRoot_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        var outside = Path.Combine(Path.GetTempPath(), "sa_tf_outside_" + Path.GetRandomFileName(), "evil.txt");

        await Assert.ThrowsAsync<SecurityException>(
            () => folder.ReadAsync(outside, (_, _) => Task.CompletedTask, TestTemp.Token));
    }

    [Fact]
    public async Task Download_EscapingTheRoot_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<SecurityException>(
            () => folder.ReadAsync(Path.Combine("..", "escape.txt"), (_, _) => Task.CompletedTask, TestTemp.Token));
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.ReadAsync("data/a/../b.txt", (_, _) => Task.CompletedTask, TestTemp.Token));
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.ReadAsync("data/we~ird.txt", (_, _) => Task.CompletedTask, TestTemp.Token));
    }

    [Fact]
    public async Task Download_MissingFile_ReturnsFalse_AndSkipsTheCallback()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var invoked = false;
        var found = await folder.ReadAsync(
            Path.Combine("nope", "f.bin"),
            (_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            TestTemp.Token);

        Assert.False(found);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Download_PointingAtADirectory_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        Directory.CreateDirectory(Path.Combine(_root, "adir"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.ReadAsync("adir", (_, _) => Task.CompletedTask, TestTemp.Token));
    }

    [Fact]
    public async Task Download_EmptyPath_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<ArgumentException>(
            () => folder.ReadAsync("  ", (_, _) => Task.CompletedTask, TestTemp.Token));
    }

    // ---------- DeleteFileAsync ----------

    [Fact]
    public async Task Delete_RelativePath_RemovesTheFile()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var (_, absolute) = await folder.WriteAsync(
            new MemoryStream("bye"u8.ToArray()), Path.Combine("d", "f.bin"), TestTemp.Token);

        Assert.True(await folder.DeleteFileAsync(Path.Combine("d", "f.bin"), TestTemp.Token));
        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task Delete_AbsolutePathInsideRoot_RemovesTheFile()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var (_, absolute) = await folder.WriteAsync(
            new MemoryStream("abs"u8.ToArray()), Path.Combine("abs", "deep", "file.txt"), TestTemp.Token);

        Assert.True(await folder.DeleteFileAsync(absolute, TestTemp.Token));
        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task Delete_AbsolutePathOutsideRoot_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        var outside = Path.Combine(Path.GetTempPath(), "sa_tf_outside_" + Path.GetRandomFileName());
        File.WriteAllText(outside, "not mine");

        try
        {
            await Assert.ThrowsAsync<SecurityException>(
                () => folder.DeleteFileAsync(outside, TestTemp.Token));
            Assert.True(File.Exists(outside)); // rejected paths are never touched
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Delete_EscapingTheRoot_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<SecurityException>(
            () => folder.DeleteFileAsync(Path.Combine("..", "escape.txt"), TestTemp.Token));
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.DeleteFileAsync("data/a/../b.txt", TestTemp.Token));
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.DeleteFileAsync("data/we~ird.txt", TestTemp.Token));
    }

    [Fact]
    public async Task Delete_MissingFile_ReturnsFalse()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        Assert.False(await folder.DeleteFileAsync(Path.Combine("nope", "f.bin"), TestTemp.Token));
    }

    [Fact]
    public async Task Delete_PointingAtADirectory_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        Directory.CreateDirectory(Path.Combine(_root, "adir"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.DeleteFileAsync("adir", TestTemp.Token));
        Assert.True(Directory.Exists(Path.Combine(_root, "adir")));
    }

    [Fact]
    public async Task Delete_EmptyPath_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await Assert.ThrowsAsync<ArgumentException>(
            () => folder.DeleteFileAsync("  ", TestTemp.Token));
    }

    [Fact]
    public async Task Delete_ThenSaveAgain_RecreatesTheFile()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var (relative, _) = await folder.WriteAsync(
            new MemoryStream("first"u8.ToArray()), "x/f.txt", TestTemp.Token);
        Assert.True(await folder.DeleteFileAsync(relative, TestTemp.Token));

        // The path is free again — a second delete is a miss, a re-save succeeds.
        Assert.False(await folder.DeleteFileAsync(relative, TestTemp.Token));

        await folder.WriteAsync(new MemoryStream("second"u8.ToArray()), relative, TestTemp.Token);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(_root, relative), TestTemp.Token));
    }

    // ---------- EnumerateFilesAsync ----------

    [Fact]
    public async Task Enumerate_MatchesPattern_AndScopesToTheSubfolder()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await folder.WriteAsync(new MemoryStream(), "data/one.txt", TestTemp.Token);
        await folder.WriteAsync(new MemoryStream(), "data/two.log", TestTemp.Token);
        await folder.WriteAsync(new MemoryStream(), "data/nested/three.txt", TestTemp.Token);
        await folder.WriteAsync(new MemoryStream(), "root.txt", TestTemp.Token);

        var flat = new List<string>();
        await foreach (var file in folder.EnumerateFilesAsync("*.txt", "data", recursive: false, TestTemp.Token))
        {
            flat.Add(file);
        }

        Assert.Equal(new[] { Path.Combine(_root, "data", "one.txt") }, flat);

        var deep = new List<string>();
        await foreach (var file in folder.EnumerateFilesAsync("*.txt", "data", recursive: true, TestTemp.Token))
        {
            deep.Add(file);
        }

        Assert.Equal(2, deep.Count);
        Assert.All(deep, p => Assert.True(Path.IsPathRooted(p)));

        var fromRoot = new List<string>();
        await foreach (var file in folder.EnumerateFilesAsync("*.txt", cancellationToken: TestTemp.Token))
        {
            fromRoot.Add(file);
        }

        Assert.Contains(Path.Combine(_root, "root.txt"), fromRoot);
    }

    [Fact]
    public async Task Enumerate_MissingSubfolder_YieldsNothing()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var seen = 0;
        await foreach (var _ in folder.EnumerateFilesAsync("*", "not-there", cancellationToken: TestTemp.Token))
        {
            seen++;
        }

        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task Enumerate_WithInjectionCharacterInSubfolder_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        // The pattern keeps its '*'/'?' (it is a pattern, not a path) — the subfolder does not.
        await Assert.ThrowsAsync<SecurityException>(
            async () =>
            {
                await foreach (var _ in folder.EnumerateFilesAsync("*.txt", "da>ta", cancellationToken: TestTemp.Token))
                {
                }
            });
    }

    [Fact]
    public void Operations_AfterDispose_AreRejected()
    {
        var provider = Build();
        var folder = Resolve(provider);

        folder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => folder.CreateSubfolder());
        provider.Dispose();
    }

    /// <summary>
    /// Seekable source of a known length that blocks on its first read — holds the save at the
    /// point where the target file already exists (preallocated) but holds no copied bytes yet.
    /// </summary>
    private sealed class GatedSeekableStream(byte[] data, TaskCompletionSource entered, Task release) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)value;
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
}
