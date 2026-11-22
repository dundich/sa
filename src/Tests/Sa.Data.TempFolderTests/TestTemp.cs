namespace Sa.Data.TempFolderTests;

/// <summary>Shared scratch-directory helpers for the temp-folder suites.</summary>
internal static class TestTemp
{
    /// <summary>A fresh empty directory under the system temp path.</summary>
    public static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "sa_tf_" + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A path under the system temp path that does <b>not</b> exist.</summary>
    public static string NewMissingPath()
        => Path.Combine(Path.GetTempPath(), "sa_tf_" + Path.GetRandomFileName());

    /// <summary>Best-effort recursive delete — tests must not fail on teardown.</summary>
    public static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            /* best effort */
        }
    }

    /// <summary>Backdates a folder's last write time so the age-based strategy sees it as expired.</summary>
    public static void MakeExpired(string directory, TimeSpan? age = null)
        => Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - (age ?? TimeSpan.FromHours(48)));

    /// <summary>
    /// Creates a directory symlink, or skips the current test where the platform forbids it
    /// (Windows without developer mode / the create-symlink privilege). The target need not exist:
    /// a dangling link is a link too, and the guard must still recognise it.
    /// </summary>
    public static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Directory symlinks are unavailable on this platform: {ex.Message}");
        }
    }

    /// <summary>Same as <see cref="CreateDirectoryLink"/>, for a file link.</summary>
    public static void CreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"File symlinks are unavailable on this platform: {ex.Message}");
        }
    }

    /// <summary>The cancellation token of the currently running test.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;
}
