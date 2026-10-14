using Microsoft.Extensions.DependencyInjection;
using Sa.Data.TempFolder;
using Sa.Data.TempFolder.Cleanup;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Age-based cleanup: expired folders go (prefix-filtered at the top level, budget-capped,
/// oldest-first), fresh ones and loose root files stay — including the nested rule: a fresh
/// top-level folder is descended into and its expired inner folders are selected, while an
/// expired top-level folder goes wholesale. Read-only instances do nothing, and a code-override
/// strategy wins over the enum.
/// </summary>
public sealed class CleanupTests : IDisposable
{
    private const string Key = "cleanup";
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

    private ITempFolder Resolve(ServiceProvider provider)
        => provider.GetRequiredKeyedService<ITempFolder>(Key);

    private string MakeSubfolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Cleanup_DeletesExpiredKeepsFreshAndLooseFiles()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var expired = MakeSubfolder("expired");
        TestTemp.MakeExpired(expired);
        var fresh = MakeSubfolder("fresh");
        var loose = Path.Combine(_root, "loose.txt");
        File.WriteAllText(loose, "old");
        File.SetLastWriteTimeUtc(loose, DateTime.UtcNow.AddHours(-48));

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(expired));
        Assert.True(Directory.Exists(fresh));
        Assert.True(File.Exists(loose)); // files directly in the root are out of scope
    }

    [Fact]
    public async Task Cleanup_OnlyConsidersFoldersWithThePrefix()
    {
        using var provider = Build(options: o => o.FolderPrefix = "p_");
        var folder = Resolve(provider);

        var prefixed = MakeSubfolder("p_old");
        TestTemp.MakeExpired(prefixed);
        var other = MakeSubfolder("other_old");
        TestTemp.MakeExpired(other);

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(prefixed));
        Assert.True(Directory.Exists(other)); // expired, but the prefix filter excludes it
    }

    [Fact]
    public async Task Cleanup_RespectsThePerPassBudget_AndTakesTheOldestFirst()
    {
        using var provider = Build(options: o => o.MaxFoldersPerPass = 2);
        var folder = Resolve(provider);

        var oldest = MakeSubfolder("e1_oldest");
        TestTemp.MakeExpired(oldest, TimeSpan.FromHours(72));
        var middle = MakeSubfolder("e2_middle");
        TestTemp.MakeExpired(middle, TimeSpan.FromHours(71));
        var youngest = MakeSubfolder("e3_youngest");
        TestTemp.MakeExpired(youngest, TimeSpan.FromHours(70));

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(oldest));
        Assert.False(Directory.Exists(middle));
        Assert.True(Directory.Exists(youngest)); // over budget — the freshest expired one waits
    }

    [Fact]
    public async Task Cleanup_AgeSourceCreationTime_AgesFromCreationNotFromWriteTime()
    {
        // The same folder judged by two clocks: backdating the write time must not expire it
        // under AgeSource=CreationTime.
        using var provider = Build(options: o => o.AgeSource = TempFolderAgeSource.CreationTime);
        var folder = Resolve(provider);

        var dir = MakeSubfolder("forged");
        TestTemp.MakeExpired(dir); // backdates the write time only

        // Portable caveat: on platforms where .NET maps Directory.GetCreationTimeUtc onto the
        // write time (some Linux setups without a usable statx birth time), the two age sources
        // are physically indistinguishable on that machine — then only "the pass completed"
        // is assertable. Where a real creation time exists, the forged write time is ignored.
        var platformDistinguishes = Directory.GetCreationTimeUtc(dir) != Directory.GetLastWriteTimeUtc(dir);

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        if (platformDistinguishes)
        {
            Assert.Equal(0, removed);
            Assert.True(Directory.Exists(dir));
        }
        else
        {
            Assert.True(removed is 0 or 1); // the pass ran to completion
        }
    }

    [Fact]
    public async Task Cleanup_AgeSourceLastWriteTime_ExpiresBackdatedFolders()
    {
        using var provider = Build(options: o => o.AgeSource = TempFolderAgeSource.LastWriteTime);
        var folder = Resolve(provider);

        var dir = MakeSubfolder("backdated");
        TestTemp.MakeExpired(dir);

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Cleanup_MaxAgeIsHonoured()
    {
        using var provider = Build(options: o => o.MaxAge = TimeSpan.FromHours(1));
        var folder = Resolve(provider);

        var tooOld = MakeSubfolder("three_hours");
        TestTemp.MakeExpired(tooOld, TimeSpan.FromHours(3));
        var young = MakeSubfolder("ten_minutes");
        TestTemp.MakeExpired(young, TimeSpan.FromMinutes(10));

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(tooOld));
        Assert.True(Directory.Exists(young));
    }

    [Fact]
    public async Task Cleanup_ReadOnlyInstance_DoesNothing()
    {
        using var provider = Build(options: o => o.ReadOnly = true);
        var folder = Resolve(provider);

        var expired = MakeSubfolder("expired");
        TestTemp.MakeExpired(expired);

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(expired));
    }

    // ---------- nested descent: "если внешняя не подходит" ----------

    [Fact]
    public async Task Cleanup_ExpiredNestedInsideAFreshTopLevel_Goes()
    {
        // The date-hierarchy case: the year container is fresh (it keeps gaining months), but a
        // month inside it has been untouched for ages — the outer folder does not fit the age
        // criterion, so the strategy descends and the inner one goes.
        using var provider = Build(options: o => o.FolderPrefix = "y_");
        var folder = Resolve(provider);

        var year = MakeSubfolder("y_2030");
        var oldMonth = Path.Combine(year, "01");
        Directory.CreateDirectory(oldMonth);
        var freshMonth = Path.Combine(year, "06");
        Directory.CreateDirectory(freshMonth);
        TestTemp.MakeExpired(oldMonth); // только месяц; год остаётся свежим

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(oldMonth));
        Assert.True(Directory.Exists(year));
        Assert.True(Directory.Exists(freshMonth));
    }

    [Fact]
    public async Task Cleanup_DescendsThroughSeveralFreshLevels()
    {
        using var provider = Build(options: o => o.FolderPrefix = "y_");
        var folder = Resolve(provider);

        var year = MakeSubfolder("y_2030");
        var month = Path.Combine(year, "06");
        Directory.CreateDirectory(month);
        var oldDay = Path.Combine(month, "01");
        Directory.CreateDirectory(oldDay);
        var freshDay = Path.Combine(month, "15");
        Directory.CreateDirectory(freshDay);
        TestTemp.MakeExpired(oldDay);

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(oldDay));
        Assert.True(Directory.Exists(freshDay));
        Assert.True(Directory.Exists(month));
        Assert.True(Directory.Exists(year));
    }

    [Fact]
    public async Task Cleanup_ExpiredTopLevel_GoesWholesaleWithItsFreshChildren()
    {
        // The other half of the rule: when the outer folder DOES fit, it is deleted whole —
        // a fresher child inside it is not spared (and is never considered on its own).
        using var provider = Build();
        var folder = Resolve(provider);

        var top = MakeSubfolder("y_old");
        var freshChild = Path.Combine(top, "fresh_child");
        Directory.CreateDirectory(freshChild);
        TestTemp.MakeExpired(top); // только топ

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(top));
        Assert.False(Directory.Exists(freshChild)); // ушёл вместе с родителем
    }

    [Fact]
    public async Task Cleanup_DoesNotDescendIntoANonPrefixedTopLevel()
    {
        // The prefix defines this instance's scope: a foreign top-level folder is skipped
        // entirely — its expired children are none of this instance's business either.
        using var provider = Build(options: o => o.FolderPrefix = "p_");
        var folder = Resolve(provider);

        var foreign = MakeSubfolder("other");
        var foreignOld = Path.Combine(foreign, "inner_old");
        Directory.CreateDirectory(foreignOld);
        TestTemp.MakeExpired(foreignOld);
        var mine = MakeSubfolder("p_new");

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(foreignOld));
        Assert.True(Directory.Exists(mine));
    }

    [Fact]
    public async Task Cleanup_FreshNestedHierarchy_IsUntouched()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var top = MakeSubfolder("fresh_top");
        var mid = Path.Combine(top, "fresh_mid");
        Directory.CreateDirectory(mid);
        var deep = Path.Combine(mid, "fresh_deep");
        Directory.CreateDirectory(deep);

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(deep));
    }

    [Fact]
    public async Task Cleanup_CodeOverrideStrategy_WinsOverTheEnum()
    {
        // The enum says AgeBased, the code says "delete everything": the override must win —
        // even folders that are seconds old go.
        using var provider = Build(configure: b => b.UseCleanupStrategy<DeleteEverythingStrategy>());
        var folder = Resolve(provider);

        var first = MakeSubfolder("f1");
        var second = MakeSubfolder("f2");

        var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
    }

    [Fact]
    public async Task Cleanup_StrategyReturningAnEscapingPath_IsSkippedNotDeleted()
    {
        using var provider = Build(configure: b => b.UseCleanupStrategy<EscapingStrategy>());
        var folder = Resolve(provider);

        var inside = MakeSubfolder("inside");

        // The sibling the hostile strategy aims at: without the guard, its "../<name>" candidate
        // would normalise straight onto this directory and delete it.
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "victim_" + Path.GetRandomFileName());
        Directory.CreateDirectory(outside);
        EscapingStrategy.TargetName = Path.GetFileName(outside);

        try
        {
            var removed = await folder.CleanupExpiredAsync(TestTemp.Token);

            // The legitimate candidate goes, the escaping one is skipped (and logged):
            // nothing outside the root is ever touched.
            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(inside));
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            TestTemp.TryDelete(outside);
        }
    }

    [Fact]
    public async Task Cleanup_VanishedCandidate_IsNotCountedAsDeleted()
    {
        // The strategy points at a folder that is not there: nothing is deleted, so the pass must
        // report 0 — a phantom "deleted" would inflate the metrics and the log line.
        using var provider = Build(configure: b => b.UseCleanupStrategy<PhantomPathStrategy>());
        var folder = Resolve(provider);

        Assert.Equal(0, await folder.CleanupExpiredAsync(TestTemp.Token));
    }

    /// <summary>Code-override sample: no age filter at all — everything top-level goes.</summary>
    private sealed class DeleteEverythingStrategy : ICleanupStrategy
    {
        public IReadOnlyList<string> SelectForDeletion(CleanupContext context)
            => [.. Directory.EnumerateDirectories(context.RootPath)];
    }

    /// <summary>A hostile/buggy strategy trying to walk out of the root.</summary>
    private sealed class EscapingStrategy : ICleanupStrategy
    {
        /// <summary>Name of the sibling directory outside the root the strategy aims at.</summary>
        public static string? TargetName { get; set; }

        public IReadOnlyList<string> SelectForDeletion(CleanupContext context)
        {
            // One legitimate candidate and one that normalises outside the root.
            var escape = Path.Combine(context.RootPath, "..", TargetName ?? "victim");
            return [Path.Combine(context.RootPath, "inside"), escape];
        }
    }

    /// <summary>Code-override sample: points at a folder that was never there.</summary>
    private sealed class PhantomPathStrategy : ICleanupStrategy
    {
        public IReadOnlyList<string> SelectForDeletion(CleanupContext context)
            => [Path.Combine(context.RootPath, "never_existed")];
    }
}
