using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;
using Sa.HybridFileStorage.FileSystem;

namespace Sa.HybridFileStorage.FileSystemTests;

/// <summary>
/// Registration helpers for the filesystem-provider tests: every storage now needs an explicit
/// <c>TempFolder</c> channel carrying its root, so these keep the call sites short.
/// </summary>
internal static class FsTestSetup
{
    /// <summary>The registration name the single-storage helpers default to.</summary>
    public const string StorageName = "fs-test";

    /// <summary>Registers a filesystem storage rooted at <paramref name="rootPath"/>.</summary>
    public static IServiceCollection AddFsStorage(
        this IServiceCollection services,
        string rootPath,
        string name = StorageName,
        Action<IFileSystemStorageBuilder>? configure = null)
        => services.AddFsStorageWithTempFolder(
            name,
            tb => tb.Options(ob => ob.Configure(o => o.RootPath = rootPath)),
            configure);

    /// <summary>Registers a filesystem storage with a fully custom temp-folder channel.</summary>
    public static IServiceCollection AddFsStorageWithTempFolder(
        this IServiceCollection services,
        string name,
        Action<ITempFolderBuilder> configureTempFolder,
        Action<IFileSystemStorageBuilder>? configure = null)
        => services.AddSaFileSystemFileStorage(name, b =>
        {
            b.TempFolder(configureTempFolder);
            configure?.Invoke(b);
        });
}
