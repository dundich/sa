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

    // ---------- SaveStreamAsync ----------

    [Fact]
    public async Task SaveStream_CreatesParents_AndReturnsBothPaths()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        var payload = "hello temp"u8.ToArray();

        await using var source = new MemoryStream(payload);
        var (relative, absolute) = await folder.SaveStreamAsync(
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
            () => folder.SaveStreamAsync(source, Path.Combine("..", "escape.txt"), TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_AbsolutePathInsideRoot_Writes()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        var absoluteTarget = Path.Combine(Path.GetFullPath(_root), "abs", "deep", "file.txt");

        await using var source = new MemoryStream("abs"u8.ToArray());
        var (relative, absolute) = await folder.SaveStreamAsync(source, absoluteTarget, TestTemp.Token);

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
            () => folder.SaveStreamAsync(source, outside, TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_WithInjectionCharacter_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.SaveStreamAsync(source, "data/we~ird.txt", TestTemp.Token).AsTask());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.SaveStreamAsync(source, "data/re>direct.txt", TestTemp.Token).AsTask());
        await Assert.ThrowsAsync<SecurityException>(
            () => folder.SaveStreamAsync(source, "data/a/../b.txt", TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_EmptyPath_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(
            () => folder.SaveStreamAsync(source, "  ", TestTemp.Token).AsTask());
    }

    [Fact]
    public async Task SaveStream_PointingAtADirectory_IsRejected()
    {
        using var provider = Build();
        var folder = Resolve(provider);
        folder.CreateSubfolder("adir");

        await using var source = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => folder.SaveStreamAsync(source, "adir", TestTemp.Token).AsTask());
    }

    // ---------- OverwriteFiles ----------

    [Fact]
    public async Task SaveStream_OverwritesAnExistingFileByDefault()
    {
        using var provider = Build(); // OverwriteFiles по умолчанию = true
        var folder = Resolve(provider);

        await folder.SaveStreamAsync(new MemoryStream("first"u8.ToArray()), "x/f.txt", TestTemp.Token);
        await folder.SaveStreamAsync(new MemoryStream("second"u8.ToArray()), "x/f.txt", TestTemp.Token);

        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(_root, "x", "f.txt"), TestTemp.Token));
    }

    [Fact]
    public async Task SaveStream_NoOverwrite_ThrowsAndKeepsTheOriginal()
    {
        using var provider = Build(options: o => o.OverwriteFiles = false);
        var folder = Resolve(provider);
        var target = Path.Combine(_root, "x", "f.txt");

        await folder.SaveStreamAsync(new MemoryStream("first"u8.ToArray()), "x/f.txt", TestTemp.Token);

        await using var again = new MemoryStream("second"u8.ToArray());
        await Assert.ThrowsAsync<IOException>(
            () => folder.SaveStreamAsync(again, "x/f.txt", TestTemp.Token).AsTask());

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

    // ---------- EnumerateFilesAsync ----------

    [Fact]
    public async Task Enumerate_MatchesPattern_AndScopesToTheSubfolder()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        await folder.SaveStreamAsync(new MemoryStream(), "data/one.txt", TestTemp.Token);
        await folder.SaveStreamAsync(new MemoryStream(), "data/two.log", TestTemp.Token);
        await folder.SaveStreamAsync(new MemoryStream(), "data/nested/three.txt", TestTemp.Token);
        await folder.SaveStreamAsync(new MemoryStream(), "root.txt", TestTemp.Token);

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
}
