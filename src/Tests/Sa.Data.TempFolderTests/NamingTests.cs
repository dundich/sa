using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Data.TempFolder;
using Sa.Data.TempFolder.Naming;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// Date-based naming: <c>{FolderPrefix}+{FolderNameFormat}</c> for every documented shape,
/// virtual clock, prefix on the first segment — and hostile/invalid formats rejected by options
/// validation before the first <c>CreateSubfolder</c>.
/// </summary>
public sealed class NamingTests : IDisposable
{
    private static readonly DateTimeOffset Stamp = new(2030, 6, 15, 14, 30, 0, TimeSpan.Zero);

    private readonly string _root = TestTemp.NewRoot();
    private readonly ManualTimeProvider _clock = new(Stamp);

    public void Dispose() => TestTemp.TryDelete(_root);

    private ServiceProvider Build(Action<ITempFolderBuilder>? configure = null, Action<TempFolderOptions>? options = null)
    {
        var services = new ServiceCollection();

        // Registered before AddSaTempFolder: its TryAddSingleton(TimeProvider.System) then
        // leaves the manual clock in place for the naming strategy (and everything else).
        services.AddSingleton<TimeProvider>(_clock);

        services.AddSaTempFolder("naming", builder =>
        {
            configure?.Invoke(builder);
            builder.Options(ob => ob.Configure(o =>
            {
                o.RootPath = _root;
                o.Naming = TempFolderNamingKind.Date;
                options?.Invoke(o);
            }));
        });
        return services.BuildServiceProvider();
    }

    private ITempFolder Resolve(ServiceProvider provider)
        => provider.GetRequiredKeyedService<ITempFolder>("naming");

    [Theory]
    [InlineData("yyyy-MM-dd", "d_2030-06-15")]
    [InlineData("yyyy-MM-dd/HH", "d_2030-06-15/14")]
    [InlineData("yyyy/MM/dd", "d_2030/06/15")]
    [InlineData("yyyy/MM/dd/HH", "d_2030/06/15/14")]
    [InlineData("yyyyMMdd", "d_20300615")]
    [InlineData("yyyyMMdd/HH", "d_20300615/14")]
    [InlineData("yyyyMM/dd", "d_203006/15")]
    [InlineData("yyyyMM/dd/HH", "d_203006/15/14")]
    public void DateNaming_GeneratesTheConfiguredShape_WithThePrefix(string format, string expected)
    {
        using var provider = Build(options: o =>
        {
            o.FolderPrefix = "d_";
            o.FolderNameFormat = format;
        });
        var folder = Resolve(provider);

        var created = folder.CreateSubfolder();

        var relative = Path.GetRelativePath(_root, created);
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), relative);
        Assert.True(Directory.Exists(created));
    }

    [Fact]
    public void DateNaming_DefaultFormat_IsYyyyMmDd()
    {
        using var provider = Build();
        var folder = Resolve(provider);

        var created = folder.CreateSubfolder();

        Assert.Equal("2030-06-15", Path.GetRelativePath(_root, created));
    }

    [Fact]
    public void DateNaming_ReusesTheFolderWithinOneBucket_AndSplitsAcrossBuckets()
    {
        using var provider = Build(options: o => o.FolderNameFormat = "yyyy/MM/dd/HH");
        var folder = Resolve(provider);

        var first = folder.CreateSubfolder();
        var second = folder.CreateSubfolder(); // same bucket → same path, CreateDirectory is idempotent
        Assert.Equal(first, second);

        _clock.Advance(TimeSpan.FromHours(1)); // 15:00 — next bucket
        var third = folder.CreateSubfolder();

        Assert.NotEqual(first, third);
        Assert.EndsWith(Path.Combine("06", "15", "15"), third, StringComparison.Ordinal);
    }

    [Fact]
    public void DateNaming_AvailableAsCodeOverride_WinsOverTheEnum()
    {
        // The enum says GuidV7, the code says Date: the override wins, and it still reads the
        // format from the options.
        using var provider = Build(
            configure: b => b.UseNamingStrategy<DateFolderNameStrategy>(),
            options: o =>
            {
                o.Naming = TempFolderNamingKind.GuidV7;
                o.FolderNameFormat = "yyyyMMdd";
            });
        var folder = Resolve(provider);

        var created = folder.CreateSubfolder();

        Assert.Equal("20300615", Path.GetRelativePath(_root, created));
    }

    [Theory]
    [InlineData(null)] // вообще не задан
    [InlineData("")]   // пусто
    [InlineData("%")]  // невалидный формат даты
    [InlineData("..")] // точечный сегмент
    [InlineData("/dd")] // rooted при пустом префиксе
    public void HostileOrBrokenFormat_FailsValidation(string? format)
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder("badfmt", b => b.Options(ob => ob.Configure(o =>
        {
            o.RootPath = _root;
            o.Naming = TempFolderNamingKind.Date;
            o.FolderNameFormat = format!;
        })));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>("badfmt"));

        Assert.Contains("FolderNameFormat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatProducingUnsafeCharacters_FailsValidation()
    {
        // "dd'~'" renders the literal tilde into every generated name — caught at resolve time,
        // before any directory is created.
        var services = new ServiceCollection();
        services.AddSaTempFolder("hostilefmt", b => b.Options(ob => ob.Configure(o =>
        {
            o.RootPath = _root;
            o.Naming = TempFolderNamingKind.Date;
            o.FolderNameFormat = "dd'~'";
        })));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>("hostilefmt"));

        Assert.Contains("FolderNameFormat", ex.Message, StringComparison.Ordinal);
    }
}
