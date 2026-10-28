using Sa.Data.TempFolder;
using Sa.HybridFileStorage.Domain;
using System.Runtime.CompilerServices;


namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// fs://share/tenant/filename
/// </summary>
/// <remarks>
/// All file I/O — writing, reading and deleting — is delegated to the keyed
/// <see cref="ITempFolder"/> this storage was registered with (see
/// <see cref="Setup.AddSaFileSystemFileStorage"/>), so the storage no longer opens
/// <c>FileStream</c>s or calls <c>File.Delete</c> itself: path guarding, cleanup activity and
/// retries belong to the temp folder, and this type only maps a file ID to a relative path and
/// shapes the result.
/// </remarks>
internal sealed class FileSystemStorage(
    ITempFolder tempFolder,
    FileSystemStorageOptions options,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private const string SchemeSeparator = "://";

    // NTFS is case-insensitive by default, so a resolved path that differs from the configured
    // root only in casing is still inside it. Same rule as PathGuard in Sa.Data.TempFolder.
    private static readonly StringComparison s_pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The temp folder hands out a fully qualified root; the trim keeps the prefix comparisons
    // below free of a trailing separator.
    private readonly string _root = Path.TrimEndingDirectorySeparator(tempFolder.RootPath);

    private readonly string _basePathScope = Path.TrimEndingDirectorySeparator(
        Path.Combine(tempFolder.RootPath, options.Basket));

    private readonly string _schemePrefix = $"{options.StorageType}{SchemeSeparator}";
    private readonly string _storageType = options.StorageType;
    private readonly bool _isReadOnly = options.IsReadOnly;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Basket => options.Basket;
    public string StorageType => _storageType;
    public bool IsReadOnly => _isReadOnly;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureWritable()
    {
        if (_isReadOnly)
            HybridFileStorageThrowHelper.ThrowWritableException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanProcess(string? fileId)
    {
        if (string.IsNullOrEmpty(fileId)) return false;

        if (!fileId.AsSpan().StartsWith(_schemePrefix.AsSpan(), StringComparison.Ordinal))
            return false;

        return IsFileIdWithinBase(fileId);
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        EnsureWritable();

        metadata.Validate();

        string filename = PathSanitizer.SanitizeRelativePath(metadata.FileName);

        // The layout and the file ID stay exactly as before: {Basket}/{tenant}/{file}, joined
        // with forward slashes. The sanitizer emits the platform separator for nested names, so it
        // is converted here — a file ID is a cross-platform identifier (`fs://share/1/sub/file.txt`
        // on every OS), and the temp folder accepts '/' on every platform. The temp folder
        // resolves and guards the path against its root.
        string relativePath = string.Concat(
            Basket,
            "/",
            metadata.TenantId.ToString(),
            "/",
            filename.Replace(Path.DirectorySeparatorChar, '/'));

        var (_, absolutePath) = await tempFolder
            .WriteAsync(fileStream, relativePath, cancellationToken)
            .ConfigureAwait(false);

        return new StorageResult(
            FileId: string.Concat(_schemePrefix, relativePath),
            AbsoluteUrl: absolutePath,
            StorageType: _storageType,
            UploadedAt: _timeProvider.GetUtcNow());
    }

    public Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        ArgumentNullException.ThrowIfNull(loadStream);

        if (!CanProcess(fileId))
            return Task.FromResult(false);

        // The temp folder already reads a missing file as `false` and rejects a directory path;
        // a non-existent file therefore keeps the provider's historical "return false" contract.
        return tempFolder.ReadAsync(FileIdToPath(fileId), loadStream, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        EnsureWritable();

        if (!CanProcess(fileId))
            return false;

        try
        {
            return await tempFolder
                .DeleteFileAsync(FileIdToPath(fileId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient failure that survived the temp folder's retries reads as "not deleted"
            // here — the same contract the provider had when it deleted files itself. The temp
            // folder retries both exception types (a permission denial is as transient as a lock
            // on Windows), so both have to be swallowed on the way out.
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FileIdToPath(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        ReadOnlySpan<char> span = fileId.AsSpan();
        int separatorIndex = span.IndexOf(SchemeSeparator.AsSpan());

        if (separatorIndex == -1)
            HybridFileStorageThrowHelper.ThrowInvalidFileIdFormat();

        return span[(separatorIndex + SchemeSeparator.Length)..].ToString();
    }

    /// <summary>
    /// Determines whether <paramref name="fileId"/> resolves to a file inside this storage's basket.
    /// </summary>
    /// <remarks>
    /// One resolve per call: the root is fully qualified, so <see cref="Path.Combine(string, string)"/>
    /// already yields an absolute path and <see cref="Path.GetFullPath(string)"/> is only there to
    /// normalise <c>..</c> and redundant separators before the containment check. <c>CanProcess</c>
    /// runs for every storage in a hybrid chain, so a second resolve would be pure overhead.
    /// </remarks>
    private bool IsFileIdWithinBase(string fileId)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_root, FileIdToPath(fileId)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A file ID is caller input and CanProcess is a predicate: an ID the OS cannot resolve
            // (a NUL character, an unsupported form, an over-long path) is "not mine", not an
            // error. The hybrid chain probes every storage with this before any failover handling,
            // so throwing here would abort the operation instead of letting the next candidate vote.
            return false;
        }

        var scope = _basePathScope.AsSpan();
        if (!full.AsSpan().StartsWith(scope, s_pathComparison))
            return false;

        // A trailing separator — or the end of the path — must follow the base scope; without it
        // a sibling basket that merely shares the prefix (`share` vs `share2`) would be claimed.
        return full.Length == scope.Length
            || full[scope.Length] == Path.DirectorySeparatorChar
            || full[scope.Length] == Path.AltDirectorySeparatorChar;
    }

    public Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (!CanProcess(fileId))
            return Task.FromResult<FileMetadata?>(null);

        if (!FileIdParser.TryParse(fileId, out _, out var tenantId, out _, out var fileName))
            return Task.FromResult<FileMetadata?>(null);

        var metadata = new FileMetadata
        {
            StorageType = StorageType,
            // The configured basket, not the one spelled in the file ID: an ID only reaches this
            // point when CanProcess mapped it inside {RootPath}/{Basket}, and reporting the
            // configured value keeps the metadata consistent with Basket even for the odd IDs the
            // parser legitimately accepts (the Postgres provider does the same).
            Basket = Basket,
            FileName = fileName,
            TenantId = tenantId
        };

        return Task.FromResult<FileMetadata?>(metadata);
    }
}
