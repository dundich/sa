using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Fixture;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;
using System.Security;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Black-box tests for PathSanitizer behavior via FileSystemStorage.UploadAsync.
/// Tests path sanitization, security, and edge cases through the public API.
/// </summary>
public sealed class PathSanitizerBlackBoxTests : IAsyncLifetime
{
    private readonly string _testDir =
        Path.Combine(Path.GetTempPath(), $"pathsanity_{Path.GetRandomFileName()}");
    private readonly CancellationTokenSource _cts = new();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_testDir);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { Directory.Delete(_testDir, true); } catch { /* ignore */ }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UploadAsync_NullMetadata_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act & Assert — metadata is required; without it there is nothing to validate or name.
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.UploadAsync(null!, FixtureHelper.GetByteStream(), _cts.Token));
    }

    [Fact]
    public async Task UploadAsync_NullFileName_ThrowsArgumentException()
    {
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = null!, TenantId = 1 },
                FixtureHelper.GetByteStream(),
                _cts.Token));
    }

    [Fact]
    public async Task UploadAsync_BlankFileName_ThrowsArgumentException()
    {
        // `UploadFileInput.Validate` owns the "cannot be null or empty" rule and runs before the
        // sanitizer, so a whitespace-only name never reaches it.
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = "   ", TenantId = 1 },
                FixtureHelper.GetByteStream(),
                _cts.Token));

        Assert.Contains("File name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_NullStream_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = "x.txt", TenantId = 1 },
                null!,
                _cts.Token));
    }

    [Fact]
    public async Task UploadAsync_NegativeTenantId_ThrowsArgumentException()
    {
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = "x.txt", TenantId = -1 },
                FixtureHelper.GetByteStream(),
                _cts.Token));

        Assert.Contains("TenantId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_EmptyFileName_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.UploadAsync(new UploadFileInput { FileName = string.Empty, TenantId = 1 }, FixtureHelper.GetByteStream(), _cts.Token));

        Assert.Contains("File name cannot be", ex.Message);
    }

    [Fact]
    public async Task UploadAsync_PathTraversalDotDot_Rejected()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act & Assert — path traversal should throw SecurityException
        var ex = await Assert.ThrowsAnyAsync<SecurityException>(() =>
            storage.UploadAsync(new UploadFileInput { FileName = "../escape.txt", TenantId = 1 }, FixtureHelper.GetByteStream(), _cts.Token));

        Assert.Contains("'..' is not allowed", ex.Message);
    }

    [Fact]
    public async Task UploadAsync_NestedPath_NormalizesSeparators()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act — upload with nested path
        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = "nested/path/file.txt", TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        // Assert — the ID is the portable shape: forward slashes on every OS, no rewrite of the
        // segments the caller supplied.
        Assert.Equal("fs://share/1/nested/path/file.txt", result.FileId);
        Assert.DoesNotContain('\\', result.FileId);
    }

    [Fact]
    public async Task UploadAsync_InjectionCharsInFileName_RejectedByPathGuard()
    {
        // The storage now writes through the temp folder, whose PathGuard rejects injection-style
        // characters outright instead of silently rewriting them. `%` is rejected on every platform
        // (it is neither a platform-invalid filename char nor a sanitizer trigger), so this is
        // deterministic rather than depending on the OS invalid-char set.
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        // Act & Assert — an injection-style character never reaches the disk.
        await Assert.ThrowsAnyAsync<SecurityException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = "file%name.txt", TenantId = 1 },
                FixtureHelper.GetByteStream(),
                _cts.Token));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task UploadAsync_DotSegment_IsRejected_EvenWithoutADotInTheFastPath(string fileName)
    {
        // The sanitizer's fast path returns a plain name untouched, so it has to route a bare "."
        // / ".." to the complex path itself — otherwise a whole-name dot segment would slip past
        // the traversal guard simply because it has no separator.
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAnyAsync<SecurityException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = fileName, TenantId = 1 },
                FixtureHelper.GetByteStream(),
                _cts.Token));
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("file ", "file")]
    [InlineData("  file.txt  ", "file.txt")]
    public async Task UploadAsync_SimpleName_IsKeptVerbatimExceptForTrimming(
        string fileName,
        string expected)
    {
        // A name without separators takes the fast path, and the fast path must agree with the
        // complex one: trim the edges, keep the rest — whether or not the name contains a dot.
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = fileName, TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        Assert.EndsWith($"/1/{expected}", result.FileId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("NUL.txt")]
    [InlineData("con.log")]
    [InlineData("COM1")]
    [InlineData("lpt9.bin")]
    [InlineData("CONOUT$")]
    [InlineData("sub/NUL.txt")]
    public async Task UploadAsync_ReservedDeviceName_IsRejected(string fileName)
    {
        // Win32 resolves the stem — before the first dot, case-insensitively — to a device, so the
        // upload would write nowhere instead of creating a file. Rejected on every platform: a File
        // ID minted on Linux must not become a device when the storage is read on Windows.
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAnyAsync<SecurityException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = fileName, TenantId = 1 },
                FixtureHelper.GetByteStream(),
                _cts.Token));
    }

    [Theory]
    [InlineData("report.txt.")]
    [InlineData("foo.")]
    [InlineData("...")]
    [InlineData("sub/dir.")]
    public async Task UploadAsync_TrailingDot_IsRejected(string fileName)
    {
        // Win32 strips a trailing dot, so `report.txt.` and `report.txt` would be one file under two
        // File IDs — an upload would silently overwrite, a delete would remove the other's file.
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        await Assert.ThrowsAnyAsync<SecurityException>(() =>
            storage.UploadAsync(
                new UploadFileInput { FileName = fileName, TenantId = 1 },
                FixtureHelper.GetByteStream(),
                _cts.Token));
    }

    [Theory]
    [InlineData("console.txt")]
    [InlineData("NULish.txt")]
    [InlineData("COM10.bin")]
    [InlineData("lpt10")]
    [InlineData(".gitignore")]
    [InlineData("v1.0")]
    public async Task UploadAsync_NamesThatOnlyLookReserved_AreAccepted(string fileName)
    {
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ILoggerFactory, NullLoggerFactory>()
            .AddFsStorage(_testDir);

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IFileStorage>();

        var result = await storage.UploadAsync(
            new UploadFileInput { FileName = fileName, TenantId = 1 },
            FixtureHelper.GetByteStream(),
            _cts.Token);

        Assert.EndsWith($"/1/{fileName}", result.FileId, StringComparison.Ordinal);
    }
}
