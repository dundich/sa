using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Data.TempFolder;

namespace Sa.Data.TempFolderTests;

/// <summary>
/// The single-field rules of <see cref="TempFolderOptions.Validate"/> that the registration suite
/// does not reach: a blank root, negative values on the bound knobs, a prefix carrying a path
/// separator, and enum values outside the defined range. Every case must surface as
/// <see cref="OptionsValidationException"/> at first resolve — never at registration.
/// </summary>
public sealed class OptionsValidationTests : IDisposable
{
    private const string Key = "validate";

    private readonly string _root = TestTemp.NewRoot();

    public void Dispose() => TestTemp.TryDelete(_root);

    /// <summary>Registers one instance, resolves it, and returns the validation failure.</summary>
    private OptionsValidationException ResolveWith(Action<TempFolderOptions> configure, bool setRoot = true)
    {
        var services = new ServiceCollection();
        services.AddSaTempFolder(Key, builder => builder.Options(ob => ob.Configure(options =>
        {
            if (setRoot)
            {
                options.RootPath = _root;
            }

            configure(options);
        })));

        using var provider = services.BuildServiceProvider();

        return Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ITempFolder>(Key));
    }

    [Fact]
    public void BlankRootPath_IsRejected()
    {
        // Post-configuration deliberately leaves a blank root alone (it only normalises a present
        // one), so the validator is what reports it.
        var ex = ResolveWith(options => options.RootPath = "   ", setRoot: false);

        Assert.Contains("RootPath cannot be empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeMaxFoldersPerPass_IsRejected()
    {
        var ex = ResolveWith(options => options.MaxFoldersPerPass = -1);

        Assert.Contains("MaxFoldersPerPass", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeCleanupInterval_IsRejected()
    {
        var ex = ResolveWith(options => options.CleanupInterval = TimeSpan.FromSeconds(-1));

        Assert.Contains("CleanupInterval", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeVolumeScanInterval_IsRejected()
    {
        var ex = ResolveWith(options => options.VolumeScanInterval = TimeSpan.FromSeconds(-1));

        Assert.Contains("VolumeScanInterval", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTouchDebounce_IsRejected()
    {
        var ex = ResolveWith(options => options.TouchDebounce = TimeSpan.FromSeconds(-1));

        Assert.Contains("TouchDebounce", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeMaxTotalSize_IsRejected()
    {
        var ex = ResolveWith(options => options.MaxTotalSize = -1);

        Assert.Contains("MaxTotalSize", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("up/")]  // the likeliest real-world typo: a prefix that is half a path
    [InlineData("up\\x")] // a Windows-style separator smuggled into the name
    public void PrefixWithAPathSeparator_IsRejected(string prefix)
    {
        var ex = ResolveWith(options => options.FolderPrefix = prefix);

        Assert.Contains("FolderPrefix", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)] // AgeSource
    [InlineData(1)] // Cleanup
    [InlineData(2)] // Naming
    public void UndefinedEnumValue_IsRejected(int which)
    {
        var ex = ResolveWith(options =>
        {
            switch (which)
            {
                case 0:
                    options.AgeSource = (TempFolderAgeSource)99;
                    break;
                case 1:
                    options.Cleanup = (TempFolderCleanupKind)99;
                    break;
                default:
                    options.Naming = (TempFolderNamingKind)99;
                    break;
            }
        });

        var expected = which switch
        {
            0 => "AgeSource",
            1 => "Cleanup strategy",
            _ => "Naming strategy",
        };

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}
