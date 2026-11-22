using Sa.Data.TempFolder.FilePathResolver;

namespace Sa.Data.TempFolderTests;

// Shared doubles for the temp-folder suites: gated / failing streams (hold a copy at a precise
// point or kill it mid-flight), an in-memory IFileSystemService and a throw-away directory under
// the system temp path.

/// <summary>
/// Source stream that blocks its first read until <paramref name="release"/> (signalling
/// <paramref name="entered"/> first), then hands out <paramref name="data"/> in chunks. Seekable
/// by default, so it also covers the preallocation path; pass <c>seekable: false</c> for the
/// "length unknown" shape.
/// </summary>
internal sealed class GatedStream(
    byte[] data,
    TaskCompletionSource entered,
    Task release,
    bool seekable = true) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => seekable;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set
        {
            if (!seekable)
            {
                throw new NotSupportedException();
            }

            _position = (int)value;
        }
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
/// Non-seekable source that yields its head and then fails — the copy dies mid-stream, which is
/// what the staged write must survive without damaging an existing target.
/// </summary>
internal sealed class FailingStream(byte[] head) : Stream
{
    private int _served;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (_served >= head.Length)
        {
            throw new IOException("The source failed mid-copy.");
        }

        var read = Math.Min(buffer.Length, head.Length - _served);
        head.AsSpan(_served, read).CopyTo(buffer.Span);
        _served += read;
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
/// In-memory <see cref="IFileSystemService"/> over a fixed set of file paths. Probes and
/// enumerations are counted so a test can assert cache behaviour.
/// </summary>
internal sealed class FakeFileSystem(params string[] files) : IFileSystemService
{
    private readonly HashSet<string> _files = [.. files.Select(Key)];

    /// <summary>Number of <see cref="FileExists"/> probes.</summary>
    public int Probes;

    /// <summary>Number of <see cref="EnumerateFiles"/> calls.</summary>
    public int Enumerations;

    public bool FileExists(string path)
    {
        Interlocked.Increment(ref Probes);
        return _files.Contains(Key(path));
    }

    public bool DirectoryExists(string path) => true;

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        Interlocked.Increment(ref Enumerations);

        var prefix = Key(directory) + "/";
        return _files
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && MatchName(Path.GetFileName(f), pattern))
            .Select(f => f.Replace('/', Path.DirectorySeparatorChar))
            .ToList();
    }

    private static bool MatchName(string name, string pattern)
        => pattern.Contains('*')
            ? name.StartsWith(pattern[..pattern.IndexOf('*')], StringComparison.Ordinal)
            : name == pattern;

    private static string Key(string path)
        => path.Replace('\\', '/').TrimEnd('/');
}

/// <summary>A throw-away directory under the system temp path, removed on dispose.</summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "sa_fpr_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Touch(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
