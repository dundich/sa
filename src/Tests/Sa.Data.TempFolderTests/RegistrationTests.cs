using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sa.Data.TempFolder;
using Sa.Data.TempFolder.Cleanup;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Registration-level behaviour: keyed multi-instances, the options pipeline (section → configure
/// → normalisation → validation), fail-fast validation — and the background service being
/// registered exactly once no matter how many instances there are.
/// </summary>
public sealed class RegistrationTests : IDisposable
{
    private readonly string _root = TestTemp.NewRoot();

    public void Dispose() => TestTemp.TryDelete(_root);

    [Fact]
    public void Register_Rejects_NullServices()
    {
        IServiceCollection services = null!;

        Assert.Throws<ArgumentNullException>(() => services.AddSaTempFolder("x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_Rejects_ABlankName(string? name)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddSaTempFolder(name!));
    }

    [Fact]
    public void Register_SameNameTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("dup");

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSaTempFolder("dup"));

        Assert.Contains("already registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_TwoNames_TwoIndependentRoots()
    {
        var otherRoot = TestTemp.NewRoot();

        try
        {
            var services = new ServiceCollection();
            services.AddSaTempFolder("first", b => b.Options(o => o.Configure(x => x.RootPath = _root)));
            services.AddSaTempFolder("second", b => b.Options(o => o.Configure(x => x.RootPath = otherRoot)));

            using var provider = services.BuildServiceProvider();

            var first = provider.GetRequiredKeyedService<ITempFolder>("first");
            var second = provider.GetRequiredKeyedService<ITempFolder>("second");

            Assert.NotSame(first, second);
            Assert.Equal(Path.GetFullPath(_root), first.RootPath);
            Assert.Equal(Path.GetFullPath(otherRoot), second.RootPath);
        }
        finally
        {
            TestTemp.TryDelete(otherRoot);
        }
    }

    [Fact]
    public void Register_InvalidOptions_FailOnResolve_NotOnRegistration()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("bad", b => b.Options(o => o.Configure(x => x.MaxAge = TimeSpan.Zero)));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>("bad"));

        Assert.Contains("MaxAge", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("..")] // path segment
    [InlineData("we>ird")] // shell redirection
    [InlineData("~tmp")] // tilde expansion
    public void Register_HostilePrefix_FailsValidation(string prefix)
    {
        // Guard: a hostile prefix must surface through the validator at resolution time —
        // it never reaches folder-name generation or the cleanup filter.
        var services = new ServiceCollection();
        services.AddSaTempFolder("prefix", b => b.Options(o => o.Configure(x => x.FolderPrefix = prefix)));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>("prefix"));

        Assert.Contains("FolderPrefix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_SystemTempRootWithoutPrefix_FailsValidation()
    {
        // The system temp folder is shared: an unscoped (empty-prefix) instance would treat every
        // top-level folder there as its own and age-based-clean it away. Reject at validation.
        var services = new ServiceCollection();
        services.AddSaTempFolder("sysscope",
            b => b.Options(o => o.Configure(x => x.RootPath = Path.GetTempPath())));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>("sysscope"));

        Assert.Contains("FolderPrefix", ex.Message, StringComparison.Ordinal);
        Assert.Contains("system temp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_SystemTempRootWithPrefix_Resolves()
    {
        // A prefix scopes the instance to its own folders — the system temp root is then usable.
        var services = new ServiceCollection();
        services.AddSaTempFolder("sysscope", b => b.Options(o => o.Configure(x =>
        {
            x.RootPath = Path.GetTempPath();
            x.FolderPrefix = "sa_";
        })));

        using var provider = services.BuildServiceProvider();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("sysscope");

        // Resolving constructs only — nothing is created in the system temp folder.
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
            Path.TrimEndingDirectorySeparator(folder.RootPath));
    }

    [Fact]
    public void Register_PrivateRootWithoutPrefix_Resolves()
    {
        // A dedicated root needs no prefix: its folders all belong to this instance anyway.
        var services = new ServiceCollection();
        services.AddSaTempFolder("private", b => b.Options(o => o.Configure(x => x.RootPath = _root)));

        using var provider = services.BuildServiceProvider();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("private");

        Assert.Equal(Path.GetFullPath(_root), folder.RootPath);
    }

    [Fact]
    public void Register_PostConfiguresRootToAFullPath()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("relative", b => b.Options(o => o.Configure(x => x.RootPath = "just_a_name")));

        using var provider = services.BuildServiceProvider();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("relative");

        Assert.True(Path.IsPathFullyQualified(folder.RootPath), $"'{folder.RootPath}' is not rooted.");
    }

    [Fact]
    public void Register_BindsScalarsFromAConfigurationSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TempFolder:RootPath"] = _root,
                ["TempFolder:MaxAge"] = "1.00:00:00",
                ["TempFolder:FolderPrefix"] = "cfg_",
                ["TempFolder:MaxFoldersPerPass"] = "7",
                ["TempFolder:Cleanup"] = "AgeBased",
                ["TempFolder:AgeSource"] = "CreationTime",
                ["TempFolder:TrackVolume"] = "true",
                ["TempFolder:MaxTotalSize"] = "1234",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSaTempFolder("cfg", b => b.FromConfiguration("TempFolder"));

        using var provider = services.BuildServiceProvider();
        var folder = (Sa.Data.TempFolder.TempFolder)provider.GetRequiredKeyedService<ITempFolder>("cfg");
        var options = folder.OptionsSnapshot;

        Assert.Equal(Path.GetFullPath(_root), options.RootPath);
        Assert.Equal(TimeSpan.FromDays(1), options.MaxAge);
        Assert.Equal("cfg_", options.FolderPrefix);
        Assert.Equal(7, options.MaxFoldersPerPass);
        Assert.Equal(TempFolderCleanupKind.AgeBased, options.Cleanup);
        Assert.Equal(TempFolderAgeSource.CreationTime, options.AgeSource);
        Assert.True(options.TrackVolume);
        Assert.Equal(1234, options.MaxTotalSize);
    }

    [Fact]
    public void Register_CodeConfigure_WinsOverTheSection_AndTheDelegateSurvivesBinding()
    {
        // The options type carries a delegate property (OnVolumeExceeded) the binder must leave
        // alone — binding the section never throws and never clears the code-assigned callback.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TempFolder:FolderPrefix"] = "from_config_",
                ["TempFolder:MaxAge"] = "1.00:00:00",
            })
            .Build();

        var invoked = false;
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSaTempFolder("code", b => b
            .FromConfiguration("TempFolder")
            .Options(ob => ob.Configure(x =>
            {
                x.FolderPrefix = "from_code_";
                x.OnVolumeExceeded = _ => invoked = true;
            })));

        using var provider = services.BuildServiceProvider();
        var folder = (Sa.Data.TempFolder.TempFolder)provider.GetRequiredKeyedService<ITempFolder>("code");

        Assert.Equal("from_code_", folder.OptionsSnapshot.FolderPrefix);
        Assert.NotNull(folder.OptionsSnapshot.OnVolumeExceeded);

        folder.OptionsSnapshot.OnVolumeExceeded!(new TempFolderVolumeArgs("code", _root, 1, 1, true));
        Assert.True(invoked);
    }

    [Fact]
    public void Register_Defaults_ApplyWhenNothingElseSetsTheValue()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("seed", b => b
            .Defaults(x => x.MaxAge = TimeSpan.FromDays(30))
            .Options(o => o.Configure(x => x.RootPath = _root)));

        using var provider = services.BuildServiceProvider();
        var folder = (Sa.Data.TempFolder.TempFolder)provider.GetRequiredKeyedService<ITempFolder>("seed");

        Assert.Equal(TimeSpan.FromDays(30), folder.OptionsSnapshot.MaxAge);
    }

    [Fact]
    public void Register_Defaults_LoseToTheSection()
    {
        // The defaults channel is the lowest precedence: a value bound from the section wins.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TempFolder:RootPath"] = _root,
                ["TempFolder:MaxAge"] = "1.00:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSaTempFolder("layered", b => b
            .Defaults(x => x.MaxAge = TimeSpan.FromDays(30))
            .FromConfiguration("TempFolder"));

        using var provider = services.BuildServiceProvider();
        var folder = (Sa.Data.TempFolder.TempFolder)provider.GetRequiredKeyedService<ITempFolder>("layered");

        Assert.Equal(TimeSpan.FromDays(1), folder.OptionsSnapshot.MaxAge);
    }

    [Fact]
    public void Register_Defaults_LoseToACodeConfigure()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("beats", b => b
            .Defaults(x => x.MaxAge = TimeSpan.FromDays(30))
            .Options(ob => ob.Configure(x =>
            {
                x.RootPath = _root;
                x.MaxAge = TimeSpan.FromDays(60);
            })));

        using var provider = services.BuildServiceProvider();
        var folder = (Sa.Data.TempFolder.TempFolder)provider.GetRequiredKeyedService<ITempFolder>("beats");

        Assert.Equal(TimeSpan.FromDays(60), folder.OptionsSnapshot.MaxAge);
    }

    [Fact]
    public void Register_ValidationSeesTheNormalisedRoot()
    {
        // RootPath is normalised (trimmed, made absolute) before validation runs — a value that
        // is only invalid because of surrounding whitespace must pass.
        var services = new ServiceCollection();
        services.AddSaTempFolder("trimmed", b => b.Options(o => o.Configure(x =>
            x.RootPath = $"  {_root}  ")));

        using var provider = services.BuildServiceProvider();
        var folder = provider.GetRequiredKeyedService<ITempFolder>("trimmed");

        Assert.Equal(Path.GetFullPath(_root), folder.RootPath);
    }

    [Fact]
    public void Register_BackgroundService_IsRegisteredOnceForManyInstances()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("a");
        services.AddSaTempFolder("b");
        services.AddSaTempFolder("c");

        using var provider = services.BuildServiceProvider();

        var hosts = provider.GetServices<IHostedService>()
            .OfType<TempFolderCleanerHost>()
            .Count();

        Assert.Equal(1, hosts);
    }

    [Fact]
    public void Register_DefaultNameOverload_ResolvesUnderTheDefaultKey()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder(b => b.Options(o => o.Configure(x => x.RootPath = _root)));

        using var provider = services.BuildServiceProvider();

        var folder = provider.GetRequiredKeyedService<ITempFolder>(Setup.DefaultName);
        Assert.Equal(Path.GetFullPath(_root), folder.RootPath);
    }

    [Fact]
    public void Register_CodeOverrideStrategies_AreOneInstancePerRegistration()
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("one", b => b
            .Options(o => o.Configure(x => x.RootPath = _root))
            .UseCleanupStrategy<TrackingCleanupStrategy>());
        services.AddSaTempFolder("two", b => b
            .Options(o => o.Configure(x => x.RootPath = _root))
            .UseCleanupStrategy<TrackingCleanupStrategy>());

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredKeyedService<TrackingCleanupStrategy>("one");
        var second = provider.GetRequiredKeyedService<TrackingCleanupStrategy>("two");

        // A stateful strategy must not leak its state across instances: each registration owns
        // its own instance, while resolving the same key twice stays a singleton.
        Assert.NotSame(first, second);
        Assert.Same(first, provider.GetRequiredKeyedService<TrackingCleanupStrategy>("one"));
    }

    /// <summary>Code-override sample with no behaviour — only identity matters here.</summary>
    private sealed class TrackingCleanupStrategy : ICleanupStrategy
    {
        public IReadOnlyList<string> SelectForDeletion(CleanupContext context) => [];
    }
}
