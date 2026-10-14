using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Sa.Data.TempFolder;

/// <summary>
/// Configuration options for one temp-folder instance registered through
/// <see cref="Setup.AddSaTempFolder"/>.
/// </summary>
/// <remarks>
/// A single mutable type served by the standard <c>Microsoft.Extensions.Options</c> pipeline:
/// section binding (<c>FromConfiguration</c>) first, then <c>Configure</c>/<c>PostConfigure</c>
/// calls made through <c>Options(...)</c>, then validation. Scalar values bind straight from
/// <c>appsettings.json</c>; <see cref="OnVolumeExceeded"/> and <see cref="OnFreeSpaceReached"/>
/// are code-only channels and are never produced by configuration binding.
/// </remarks>
public sealed class TempFolderOptions
{
    /// <summary>
    /// Gets or sets the root directory of this instance. Defaults to
    /// <see cref="Path.GetTempPath"/>. Relative values are resolved to a full path by the
    /// registration's post-configure step before validation runs.
    /// </summary>
    public string RootPath { get; set; } = Path.GetTempPath();

    /// <summary>
    /// Gets or sets how old a subfolder may be before the cleanup strategy treats it as expired.
    /// Defaults to 24 hours. Which timestamp the age is measured from is decided by
    /// <see cref="AgeSource"/>.
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the timestamp used to age subfolders. Defaults to
    /// <see cref="TempFolderAgeSource.LastWriteTime"/> — a marker the instance refreshes itself
    /// (debounced) after writes and successful reads, so active folders survive cleanup.
    /// </summary>
    public TempFolderAgeSource AgeSource { get; set; } = TempFolderAgeSource.LastWriteTime;

    /// <summary>
    /// Gets or sets the folder-name prefix. It does double duty: generated subfolder names start
    /// with it, and the age-based cleanup only considers top-level subfolders that start with it.
    /// For <see cref="TempFolderNamingKind.Date"/> the prefix lands on the first segment of the
    /// date path (<c>up_</c> + <c>yyyy/MM/dd</c> → <c>up_2026/10/08</c>), so the cleanup filter
    /// keeps matching the hierarchy it created. Defaults to <see cref="string.Empty"/>
    /// (no prefix — every top-level subfolder is in scope).
    /// </summary>
    public string FolderPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the .NET date/time format string the <see cref="TempFolderNamingKind.Date"/>
    /// strategy shapes folder names with — rendered for the current local time of the container's
    /// <see cref="TimeProvider"/> with the invariant culture and appended to
    /// <see cref="FolderPrefix"/>. Any pattern works; the intended shapes are
    /// <c>yyyy-MM-dd</c>, <c>yyyy-MM-dd/HH</c>, <c>yyyy/MM/dd</c>, <c>yyyy/MM/dd/HH</c>,
    /// <c>yyyyMMdd</c>, <c>yyyyMMdd/HH</c>, <c>yyyyMM/dd</c>, <c>yyyyMM/dd/HH</c>.
    /// Defaults to <c>yyyy-MM-dd</c>. Only consulted by the Date strategy (or a code override
    /// that reads it); the value is validated regardless.
    /// </summary>
    public string FolderNameFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>
    /// Gets or sets whether <see cref="ITempFolder.WriteAsync"/> and
    /// <see cref="ITempFolder.CopyFileAsync"/> replace a file that already exists at the target
    /// path. Defaults to <see langword="true"/> — the file is overwritten. When
    /// <see langword="false"/>, an existing file fails the write with <see cref="IOException"/>
    /// and the original stays intact.
    /// </summary>
    public bool OverwriteFiles { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of subfolders a single cleanup pass may delete.
    /// Defaults to 100. Bounds the duration of one pass; leftover expired folders are picked up
    /// by the next pass.
    /// </summary>
    public int MaxFoldersPerPass { get; set; } = 100;

    /// <summary>
    /// Gets or sets how often the background service runs a cleanup pass for this instance.
    /// Defaults to 1 hour. Must be positive.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets how often the background volume scanner runs independently of cleanup.
    /// Defaults to <see cref="TimeSpan.Zero"/> — the volume is then measured only right before a
    /// cleanup pass (when <see cref="TrackVolume"/> is on). A positive value additionally keeps the
    /// measured volume fresh between cleanup passes, so <see cref="OnVolumeExceeded"/> reacts sooner.
    /// </summary>
    public TimeSpan VolumeScanInterval { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets a value indicating whether this instance is read-only. Defaults to
    /// <see langword="false"/>. Read-only instances reject every mutating operation
    /// (<c>CreateSubfolder</c> / <c>WriteAsync</c> / <c>CopyFileAsync</c> /
    /// <c>CleanupExpiredAsync</c>) and the background service ignores them completely: no cleanup,
    /// no volume scan, no <see cref="OnVolumeExceeded"/>, no free-space check. Startup access
    /// validation then only checks that the root exists and can be listed.
    /// </summary>
    public bool ReadOnly { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum total volume of the root directory in bytes.
    /// Defaults to 0 — no limit, and no volume is ever reported. When positive (and
    /// <see cref="TrackVolume"/> is <see langword="true"/>), crossing the limit invokes
    /// <see cref="OnVolumeExceeded"/>; the event only notifies — nothing is deleted because of
    /// the volume itself, expired-folder cleanup stays age-based.
    /// </summary>
    public long MaxTotalSize { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether folder sizes are measured and cached at all.
    /// Defaults to <see langword="false"/> — the volume is then never computed and
    /// <see cref="MaxTotalSize"/> is inert. Turn on to enable the low-priority background
    /// measurement.
    /// </summary>
    public bool TrackVolume { get; set; } = false;

    /// <summary>
    /// Gets or sets the debounce delay before a file access (write or successful read) refreshes
    /// the activity markers of the touched folder chain (the accessed file's directory and every
    /// ancestor up to the root).
    /// Defaults to 5 seconds: a burst of accesses re-arms one timer per folder, and only a quiet
    /// period of this length actually updates the folders' last write times. Must not be
    /// negative.
    /// </summary>
    public TimeSpan TouchDebounce { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets which cleanup strategy runs for this instance when no code override was
    /// registered. Defaults to <see cref="TempFolderCleanupKind.AgeBased"/>.
    /// </summary>
    public TempFolderCleanupKind Cleanup { get; set; } = TempFolderCleanupKind.AgeBased;

    /// <summary>
    /// Gets or sets which folder-naming strategy runs for this instance when no code override was
    /// registered. Defaults to <see cref="TempFolderNamingKind.GuidV7"/>.
    /// </summary>
    public TempFolderNamingKind Naming { get; set; } = TempFolderNamingKind.GuidV7;

    /// <summary>
    /// Gets or sets the callback invoked on a volume-limit transition — edge-triggered, once when
    /// the measured volume crosses <see cref="MaxTotalSize"/> and once when it falls back under.
    /// Code-only: configuration binding never touches this property. Exceptions thrown by the
    /// callback are caught and logged, never propagated into the scanner.
    /// </summary>
    public Action<TempFolderVolumeArgs>? OnVolumeExceeded { get; set; }

    /// <summary>
    /// Gets or sets the callback invoked on a free-space-limit transition — edge-triggered, once
    /// when the available space drops to or below <see cref="MinFreeSpace"/> and once when it
    /// climbs back above. Code-only: configuration binding never touches this property. Exceptions
    /// thrown by the callback are caught and logged, never propagated into the monitor.
    /// </summary>
    public Action<TempFolderFreeSpaceArgs>? OnFreeSpaceReached { get; set; }

    /// <summary>
    /// Gets or sets the minimum number of free bytes the volume holding the root should keep.
    /// Defaults to <c>0</c> — the check is off and no free space is ever probed. When positive,
    /// the background service polls the volume every <see cref="FreeSpaceCheckInterval"/> and
    /// invokes <see cref="OnFreeSpaceReached"/> edge-triggered on reaching (or recovering from)
    /// this limit. The event only notifies — nothing is deleted because of the free space itself.
    /// </summary>
    public long MinFreeSpace { get; set; } = 0;

    /// <summary>
    /// Gets or sets how often the background service polls the free space of the root's volume.
    /// Defaults to 24 hours — the first check runs right at host start, then once per interval.
    /// Must be positive; only consulted when <see cref="MinFreeSpace"/> is positive.
    /// </summary>
    public TimeSpan FreeSpaceCheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Validates the current configuration and throws a <see cref="ValidationException"/> if any
    /// property is invalid.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when a property value is not usable.</exception>
    /// <remarks>
    /// Called by <see cref="TempFolderOptionsValidator"/> after post-configuration, so it validates
    /// normalised values (a fully resolved <see cref="RootPath"/>, trimmed <see cref="FolderPrefix"/>).
    /// Explicit checks rather than <c>ValidateDataAnnotations()</c>: the latter is marked
    /// <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new ValidationException("RootPath cannot be empty.");
        }

        try
        {
            Path.GetFullPath(RootPath);

            if (RootPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new ValidationException($"RootPath contains invalid characters: {RootPath}");
            }
        }
        catch (Exception ex) when (ex is not ValidationException)
        {
            throw new ValidationException($"Invalid RootPath format: {RootPath}. {ex.Message}");
        }

        if (MaxAge <= TimeSpan.Zero)
        {
            throw new ValidationException($"MaxAge must be positive, but was {MaxAge}.");
        }

        if (FolderPrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            FolderPrefix is "." or ".." ||
            FolderPrefix.Contains(Path.DirectorySeparatorChar) ||
            FolderPrefix.Contains(Path.AltDirectorySeparatorChar) ||
            PathGuard.HasUnsafeCharacters(FolderPrefix))
        {
            throw new ValidationException(
                $"FolderPrefix must be a plain name prefix: no path separators, no '..', " +
                $"and no injection-style characters ('~', '>', shell metacharacters, control characters): " +
                $"'{FolderPrefix}'.");
        }

        // FolderNameFormat is validated whatever Naming says: a broken format must fail loudly at
        // resolve/start, not silently inside a strategy at first CreateSubfolder.
        if (string.IsNullOrWhiteSpace(FolderNameFormat))
        {
            throw new ValidationException("FolderNameFormat cannot be empty.");
        }

        string formatSample;
        try
        {
            formatSample = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero)
                .ToString(FolderNameFormat, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ValidationException(
                $"FolderNameFormat is not a valid date/time format: '{FolderNameFormat}'. {ex.Message}");
        }

        if (formatSample.Length == 0)
        {
            throw new ValidationException($"FolderNameFormat '{FolderNameFormat}' produces an empty folder name.");
        }

        // Exactly what DateFolderNameStrategy would hand to PathGuard for this sample: date
        // digits are constant, so the sample's safety is the safety of every generated name.
        var generatedSample = $"{FolderPrefix}{formatSample}";
        if (generatedSample is "." ||
            PathGuard.HasUnsafeCharacters(generatedSample) ||
            Path.IsPathRooted(generatedSample))
        {
            throw new ValidationException(
                $"FolderNameFormat '{FolderNameFormat}' with prefix '{FolderPrefix}' produces an " +
                $"unusable folder path '{generatedSample}'.");
        }

        if (MaxFoldersPerPass <= 0)
        {
            throw new ValidationException($"MaxFoldersPerPass must be positive, but was {MaxFoldersPerPass}.");
        }

        if (CleanupInterval <= TimeSpan.Zero)
        {
            throw new ValidationException($"CleanupInterval must be positive, but was {CleanupInterval}.");
        }

        if (VolumeScanInterval < TimeSpan.Zero)
        {
            throw new ValidationException($"VolumeScanInterval cannot be negative, but was {VolumeScanInterval}.");
        }

        if (TouchDebounce < TimeSpan.Zero)
        {
            throw new ValidationException($"TouchDebounce cannot be negative, but was {TouchDebounce}.");
        }

        if (MaxTotalSize < 0)
        {
            throw new ValidationException($"MaxTotalSize cannot be negative, but was {MaxTotalSize}.");
        }

        if (MinFreeSpace < 0)
        {
            throw new ValidationException($"MinFreeSpace cannot be negative, but was {MinFreeSpace}.");
        }

        if (FreeSpaceCheckInterval <= TimeSpan.Zero)
        {
            throw new ValidationException(
                $"FreeSpaceCheckInterval must be positive, but was {FreeSpaceCheckInterval}.");
        }

        if (!Enum.IsDefined(AgeSource))
        {
            throw new ValidationException($"Unknown AgeSource value: {AgeSource}.");
        }

        if (!Enum.IsDefined(Cleanup))
        {
            throw new ValidationException($"Unknown Cleanup strategy value: {Cleanup}.");
        }

        if (!Enum.IsDefined(Naming))
        {
            throw new ValidationException($"Unknown Naming strategy value: {Naming}.");
        }

        // Cross-field scope check — deliberately last, so the single-field messages above are
        // reported first. The system temp directory is shared with every other process: with an
        // empty FolderPrefix the age-based cleanup would treat *every* top-level folder under it
        // as this instance's own and delete it. Require an explicit scope instead of silently
        // cleaning up other applications' files.
        if (string.IsNullOrEmpty(FolderPrefix) && IsSystemTempDirectory(RootPath))
        {
            throw new ValidationException(
                $"RootPath '{RootPath}' is the system temp directory ('{Path.GetTempPath()}'), which is " +
                "shared with other processes — age-based cleanup without a scope would delete their folders. " +
                "Set FolderPrefix so only this instance's folders are in scope, or point RootPath at a " +
                "dedicated directory.");
        }
    }

    /// <summary>
    /// True when <paramref name="rootPath"/> normalises to the machine's system temp directory
    /// (the shared folder an unscoped cleanup would treat as this instance's own). A malformed
    /// path is not this check's problem — <see cref="Validate"/> already reported it.
    /// </summary>
    private static bool IsSystemTempDirectory(string rootPath)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
