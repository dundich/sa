namespace Sa.Data.TempFolder.Cleanup;

/// <summary>
/// The default cleanup strategy: subfolders whose age exceeds
/// <see cref="TempFolderOptions.MaxAge"/>. An expired <b>top-level</b> subfolder (filtered by
/// <see cref="TempFolderOptions.FolderPrefix"/>) goes wholesale together with everything inside
/// it; a top-level subfolder that is still fresh is <b>descended into</b>, and the same rule
/// applies at every inner level recursively. That is what makes date hierarchies
/// (<c>yyyy/MM/dd/HH</c>, <c>yyyyMMdd</c> …) work: the <c>2026</c> container stays for a year
/// while its long-old months inside are shed, and an expired month takes its days with it without
/// anyone walking them first. Oldest first across all levels, capped at
/// <see cref="TempFolderOptions.MaxFoldersPerPass"/>.
/// </summary>
/// <remarks>
/// Files lying directly in the root are never selected. The prefix filter applies to top-level
/// folders <b>only</b>: nested folders are the instance's own structure (the date path under a
/// prefixed first segment), so re-filtering them by prefix would exempt every inner level.
/// The age is measured from <see cref="TempFolderOptions.AgeSource"/>; with the default
/// <see cref="TempFolderAgeSource.LastWriteTime"/> an actively accessed folder (written to or
/// read from) keeps refreshing its marker (debounced — the accessed file's own directory and all
/// of its ancestors) and therefore survives cleanup at every level.
/// <para>
/// The descent stops at <see cref="MaxSearchDepth"/> and skips folders that vanish or turn
/// unreadable mid-pass — one bad subtree must not cost the whole cleanup.
/// </para>
/// </remarks>
public sealed class AgeBasedCleanupStrategy : ICleanupStrategy
{
    /// <summary>
    /// How far below the root the descent looks. Date hierarchies are 1–4 levels deep; the cap
    /// bounds pathological trees (symlink cycles, a runaway custom strategy's output).
    /// </summary>
    public const int MaxSearchDepth = 16;

    /// <inheritdoc />
    public IReadOnlyList<string> SelectForDeletion(CleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options;
        var prefix = options.FolderPrefix;
        var now = context.TimeProvider.GetUtcNow().UtcDateTime;
        var candidates = new List<(string Path, DateTime Stamp)>();

        foreach (var dir in Enumerate(context.RootPath))
        {
            var name = Path.GetFileName(dir);

            if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Outside this instance's scope — and deliberately not descended into: the
                // children of a foreign top-level folder are none of this instance's business.
                continue;
            }

            Select(dir, depth: 0, now, options, candidates);
        }

        // Oldest first: when the per-pass budget cuts the list, the freshest of the expired
        // folders — at any level — are the ones left for the next pass.
        candidates.Sort(static (a, b) => a.Stamp.CompareTo(b.Stamp));

        if (candidates.Count > options.MaxFoldersPerPass)
        {
            candidates.RemoveRange(options.MaxFoldersPerPass, candidates.Count - options.MaxFoldersPerPass);
        }

        var result = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            result[i] = candidates[i].Path;
        }

        return result;
    }

    /// <summary>
    /// One node of the descent: expired → the whole subtree is a candidate (no reason to look
    /// inside what is being deleted); fresh → recurse into the children with the same rule.
    /// </summary>
    private static void Select(
        string dir,
        int depth,
        DateTime now,
        TempFolderOptions options,
        List<(string Path, DateTime Stamp)> candidates)
    {
        DateTime stamp;
        try
        {
            stamp = options.AgeSource switch
            {
                TempFolderAgeSource.CreationTime => Directory.GetCreationTimeUtc(dir),
                _ => Directory.GetLastWriteTimeUtc(dir),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // vanished between enumeration and inspection
        }

        if (now - stamp > options.MaxAge)
        {
            candidates.Add((dir, stamp));
            return;
        }

        if (depth >= MaxSearchDepth)
        {
            return;
        }

        foreach (var child in Enumerate(dir))
        {
            Select(child, depth + 1, now, options, candidates);
        }
    }

    /// <summary>Materialises one directory level, skipping subtrees that cannot be listed.</summary>
    private static List<string> Enumerate(string directory)
    {
        var children = new List<string>();

        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                children.Add(child);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable subtree: skip it, the rest of the pass continues.
        }

        return children;
    }
}
