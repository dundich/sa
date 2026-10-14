using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sa.Data.TempFolder.FilePathResolver;

/// <summary>
/// Resolves a path stored in a database into a real path on the file system,
/// optionally returning the result relative to a configured root.
/// </summary>
/// <remarks>
/// Registered through <see cref="Setup"/> (<c>AddFilePathResolver</c>) and resolved by key:
/// <c>sp.GetRequiredKeyedService&lt;IFilePathResolver&gt;(name)</c>.
/// </remarks>
public interface IFilePathResolver
{
    /// <summary>
    /// Resolves the system path from the database path.
    /// </summary>
    /// <param name="dbPath">The path as stored in the database.</param>
    /// <param name="asRelativePath">
    /// When <c>true</c>, the result is returned relative to <see cref="FilePathResolver.ConfiguredPath"/>
    /// whenever the resolved path lies under that root.
    /// </param>
    /// <returns>The resolved system path, or <c>null</c> when the file cannot be located.</returns>
    string? ResolvePath(string dbPath, bool asRelativePath = false);
}

/// <summary>
/// A minimal abstraction over file-system probes: all disk access of
/// <see cref="FilePathResolver"/> goes through it, so it can be unit-tested without touching
/// the real disk — and swapped per DI registration (a keyed instance first, then a plain one,
/// else <see cref="DefaultFileSystemService"/>).
/// </summary>
public interface IFileSystemService
{
    /// <summary>True when <paramref name="path"/> refers to an existing file.</summary>
    bool FileExists(string path);

    /// <summary>True when <paramref name="path"/> refers to an existing directory.</summary>
    bool DirectoryExists(string path);

    /// <summary>Enumerates file paths under <paramref name="directory"/> matching <paramref name="pattern"/>.</summary>
    IEnumerable<string> EnumerateFiles(string directory, string pattern);
}

/// <summary>
/// Default <see cref="IFileSystemService"/> backed by <see cref="File"/> and <see cref="Directory"/>.
/// </summary>
internal sealed class DefaultFileSystemService : IFileSystemService
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
        => Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories);
}

/// <summary>
/// Resolves fuzzy / imprecise paths (as stored in a database) into real paths on the file system.
/// <para>
/// Resolution cascades: literal absolute path → path relative to <see cref="ConfiguredPath"/> →
/// directory-suffix match under <see cref="ConfiguredPath"/> → (optional) deep search.
/// Each discovered mapping is remembered per file, so subsequent lookups for the same
/// database path are a single dictionary read plus at most a few <c>stat</c> calls.
/// </para>
/// </summary>
[DebuggerStepThrough]
public sealed class FilePathResolver : IFilePathResolver
{
    /// <summary>Enum representing the kind of mapping used to resolve a path.</summary>
    public enum ResolvedPathType
    {
        /// <summary>No mapping has been established.</summary>
        None,

        /// <summary>The database path is a literal absolute path on this machine.</summary>
        Direct,

        /// <summary>
        /// The database path's directory is mapped onto a sub-directory of <see cref="ConfiguredPath"/>.
        /// A path that is simply relative to <see cref="ConfiguredPath"/> is the special case
        /// where both <c>MapDir</c> and <c>XDir</c> are empty.
        /// </summary>
        Map,
    }

    private static readonly char[] s_separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>Immutable per-file resolution result.</summary>
    private readonly struct Resolution(ResolvedPathType type, string mapDir, string xDir)
    {
        public readonly ResolvedPathType Type = type;

        /// <summary>The database-path prefix to strip before mapping onto <see cref="ConfiguredPath"/>.</summary>
        public readonly string MapDir = mapDir;

        /// <summary>The prefix (under <see cref="ConfiguredPath"/>) the file lives in.</summary>
        public readonly string XDir = xDir;
    }

    private static readonly Resolution s_none = new(ResolvedPathType.None, string.Empty, string.Empty);

    private readonly IFileSystemService _fileSystem;
    private readonly ConcurrentDictionary<string, Resolution> _cache = new();

    private HashSet<string>? _possibleExtensions;
    private Resolution? _externalMap;
    private volatile bool _isDeepSearch;
    private volatile bool _isForceSearch;
    private int _hasResolved;

    /// <summary>Raised once, when the first mapping is established.</summary>
    public event EventHandler? Resolved;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilePathResolver"/> class.
    /// </summary>
    /// <param name="fileSystem">Abstraction over file-system probes.</param>
    /// <param name="configuredPath">The root directory used for file search.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="configuredPath"/> is null or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="configuredPath"/> does not exist.</exception>
    public FilePathResolver(IFileSystemService fileSystem, string configuredPath)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        ConfiguredPath = Normalize(configuredPath);
        if (!_fileSystem.DirectoryExists(ConfiguredPath))
            throw new DirectoryNotFoundException($"Configured path '{ConfiguredPath}' does not exist.");
    }

    /// <summary>Creates a <see cref="FilePathResolver"/> over <see cref="DefaultFileSystemService"/>.</summary>
    public static FilePathResolver Create(string configuredPath)
        => new(new DefaultFileSystemService(), configuredPath);

    /// <summary>The configured root directory path for file search (no trailing separator).</summary>
    public string ConfiguredPath { get; }

    /// <summary>The file extensions (with a leading dot) considered during fuzzy search, or empty.</summary>
    public IReadOnlyCollection<string> PossibleExtensions
        => _possibleExtensions is { } ext ? (IReadOnlyCollection<string>)ext : [];

    /// <summary>Indicates whether the path has been successfully resolved at least once.</summary>
    public bool IsResolved => _hasResolved != 0;

    /// <summary>Indicates whether deep (recursive) search is enabled.</summary>
    public bool IsDeepSearch => _isDeepSearch;

    /// <summary>Indicates whether force search is enabled (re-resolves instead of reading the cache).</summary>
    public bool IsForceSearchEnabled => _isForceSearch;

    /// <summary>Sets the file extensions used to disambiguate files during fuzzy search.</summary>
    public FilePathResolver WithPossibleExtensions(params string[] possibleExtensions)
    {
        _possibleExtensions = [.. possibleExtensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.StartsWith('.') ? e : $".{e}")
            .Distinct()];
        return this;
    }

    /// <summary>
    /// Sets an explicit global mapping: database paths that start with <paramref name="map"/>
    /// are looked up directly under <see cref="ConfiguredPath"/> (the remainder being the relative
    /// file path). The mapping applies even before any per-file resolution has happened.
    /// </summary>
    public FilePathResolver WithMap(string map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(map);
        _externalMap = new Resolution(ResolvedPathType.Map, Normalize(map), string.Empty);
        RaiseResolvedIfNeeded();
        return this;
    }

    /// <summary>Enables or disables deep (recursive) search.</summary>
    public FilePathResolver WithDeepSearch(bool deep)
    {
        _isDeepSearch = deep;
        return this;
    }

    /// <summary>
    /// Enables or disables force search. When enabled, every <see cref="ResolvePath"/> re-runs the
    /// full search cascade and rewrites the per-file cache instead of reading it.
    /// </summary>
    public FilePathResolver WithForceSearch(bool force)
    {
        _isForceSearch = force;
        return this;
    }

    /// <summary>Resolves the file-system path from the database path (see <see cref="IFilePathResolver.ResolvePath"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is null or whitespace.</exception>
    public string? ResolvePath(string dbPath, bool asRelativePath = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var normalized = Normalize(dbPath);

        // Fast path: a mapping already known for this exact database path.
        if (!_isForceSearch
            && _cache.TryGetValue(normalized, out var cached)
            && cached.Type != ResolvedPathType.None
            && TryResolveCached(cached, normalized) is { } hit)
        {
            return ToRelative(hit, asRelativePath);
        }

        // An explicit global mapping (WithMap) applies before the cascade, even on a cache miss.
        if (_externalMap is { } map
            && TryResolveCached(map, normalized) is { } ext)
        {
            _cache[normalized] = map;
            return ToRelative(ext, asRelativePath);
        }

        var (full, res) = Search(normalized);
        if (full is null)
            return null;

        // Remember the mapping for next time. Deep-search hits are intentionally not cached:
        // a file found anywhere in the tree carries no reliable directory mapping.
        if (res.Type != ResolvedPathType.None)
        {
            _cache[normalized] = res;
            RaiseResolvedIfNeeded();
        }

        return ToRelative(full, asRelativePath);
    }

    /// <summary>Resolves a previously cached mapping back to a concrete path, or <c>null</c> when the file has vanished.</summary>
    private string? TryResolveCached(Resolution res, string dbPath)
    {
        return res.Type switch
        {
            ResolvedPathType.Direct => _fileSystem.FileExists(dbPath) ? dbPath : null,

            ResolvedPathType.Map =>
                MatchesMap(res, dbPath)
                    ? TryFindFile(Path.Combine(ConfiguredPath, res.XDir), StripMap(res, dbPath))
                    : null,

            _ => null,
        };
    }

    /// <summary>The resolution cascade: direct → relative → incremental → (optional) deep search.</summary>
    private (string? Full, Resolution Res) Search(string dbPath)
    {
        var (full, res) = DirectSearch(dbPath);
        if (full != null)
            return (full, res);

        (full, res) = RelativeSearch(dbPath);
        if (full != null)
            return (full, res);

        (full, res) = IncrementalSearch(dbPath);
        if (full != null)
            return (full, res);

        if (_isDeepSearch)
            return (DeepSearch(dbPath), s_none);

        return (null, s_none);
    }

    /// <summary>Looks the database path up literally, when it is an absolute path that exists.</summary>
    private (string? Full, Resolution Res) DirectSearch(string dbPath)
    {
        if (!Path.IsPathRooted(dbPath))
            return (null, s_none);

        return _fileSystem.FileExists(dbPath)
            ? (dbPath, new Resolution(ResolvedPathType.Direct, string.Empty, string.Empty))
            : (null, s_none);
    }

    /// <summary>Looks the database path up relative to <see cref="ConfiguredPath"/>.</summary>
    private (string? Full, Resolution Res) RelativeSearch(string dbPath)
    {
        if (Path.IsPathRooted(dbPath))
            return (null, s_none);

        var full = TryFindFile(ConfiguredPath, dbPath);
        return full is null
            ? (null, s_none)
            : (full, new Resolution(ResolvedPathType.Map, string.Empty, string.Empty));
    }

    /// <summary>
    /// Matches the database directory's trailing components against directories under
    /// <see cref="ConfiguredPath"/> — innermost first. The first hit fixes both halves of the
    /// mapping, so no post-hoc suffix reconstruction is required.
    /// </summary>
    private (string? Full, Resolution Res) IncrementalSearch(string dbPath)
    {
        var fileName = Path.GetFileName(dbPath);
        if (fileName.Length == 0)
            return (null, s_none);

        var dbDir = Path.GetDirectoryName(dbPath) ?? string.Empty;
        var parts = dbDir.Split(s_separators, StringSplitOptions.RemoveEmptyEntries);

        // No directory part: the file lives directly in the configured root.
        if (parts.Length == 0)
            return TryFindFile(ConfiguredPath, fileName) is { } root
                ? (root, new Resolution(ResolvedPathType.Map, string.Empty, string.Empty))
                : (null, s_none);

        // Walk outward, one trailing component at a time:
        //   i = n-1 → ConfiguredPath/<last part>
        //   i = n-2 → ConfiguredPath/<last-1>/<last>
        //   ...
        //   i = 0   → ConfiguredPath/<all parts>
        // xDir is exactly the directory probed, so the cached mapping re-derives the same path.
        var mapDir = Normalize(dbDir); // keep dbDir as it appears in dbPath (leading separator
                                       // included) so the cached prefix always prefix-matches.
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var xDir = string.Join(Path.DirectorySeparatorChar, parts[i..]);
            var found = TryFindFile(Path.Combine(ConfiguredPath, xDir), fileName);
            if (found is null)
                continue;

            return (found, new Resolution(ResolvedPathType.Map, mapDir, xDir));
        }

        return (null, s_none);
    }

    /// <summary>Recursively searches <see cref="ConfiguredPath"/> by file name (optionally by extension). The result is not cached.</summary>
    private string? DeepSearch(string dbPath)
    {
        var name = Path.GetFileName(dbPath);
        if (name.Length == 0)
            return null;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0)
            return null;

        var pattern = _possibleExtensions is { } ext && ext.Count > 0
            ? $"{stem}.*"
            : name;

        var files = _fileSystem.EnumerateFiles(ConfiguredPath, pattern);

        if (_possibleExtensions is { } allowed && allowed.Count > 0)
            files = files.Where(f => allowed.Contains(Path.GetExtension(f)));

        return files.FirstOrDefault();
    }

    /// <summary>
    /// Returns <c>dir</c>/<paramref name="fileName"/> when it exists, otherwise tries each
    /// configured extension in turn. A single probe when no extensions are configured.
    /// </summary>
    private string? TryFindFile(string dir, string fileName)
    {
        if (_fileSystem.FileExists(Path.Combine(dir, fileName)))
            return Path.Combine(dir, fileName);

        var ext = _possibleExtensions;
        if (ext is null || ext.Count == 0)
            return null;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var candidate in ext)
        {
            var full = Path.Combine(dir, stem + candidate);
            if (_fileSystem.FileExists(full))
                return full;
        }

        return null;
    }

    /// <summary>True when <paramref name="dbPath"/> lies at or under the mapping prefix — on a component boundary.</summary>
    private static bool MatchesMap(Resolution res, string dbPath)
        => res.MapDir.Length == 0
            || (dbPath.StartsWith(res.MapDir, StringComparison.Ordinal)
                && (dbPath.Length == res.MapDir.Length || Array.IndexOf(s_separators, dbPath[res.MapDir.Length]) >= 0));

    /// <summary>Strips the mapping prefix from a database path, keeping the remainder relative to <see cref="ConfiguredPath"/>.</summary>
    private static string StripMap(Resolution res, string dbPath)
        => res.MapDir.Length == 0 ? dbPath : TrimStartSeparators(dbPath[res.MapDir.Length..]);

    private static string TrimStartSeparators(string s)
        => s.TrimStart(s_separators);

    private void RaiseResolvedIfNeeded()
    {
        if (Interlocked.Exchange(ref _hasResolved, 1) == 0)
            Resolved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns <paramref name="full"/> as-is when it is not under <see cref="ConfiguredPath"/>,
    /// otherwise relative to that root.
    /// </summary>
    private string ToRelative(string full, bool asRelativePath)
    {
        if (!asRelativePath)
            return full;

        return full.StartsWith(ConfiguredPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.GetRelativePath(ConfiguredPath, full)
            : full;
    }

    /// <summary>Normalizes separators to the platform default and drops any trailing separator.</summary>
    private static string Normalize(string path)
        => path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(s_separators);
}
