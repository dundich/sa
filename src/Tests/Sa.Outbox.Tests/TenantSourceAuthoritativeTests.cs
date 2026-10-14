using Sa.Outbox.Partitional;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests;

/// <summary>
/// Verifies that every "explicit tenant source" builder marks that source authoritative by
/// disabling auto-detection. The bug these tests pin: <see cref="TenantSettings.WithTenantIds"/>,
/// <see cref="TenantSettings.WithTenantSource"/> and
/// <see cref="TenantSettings.WithTenantProvider"/> must flip <c>AutoDetect</c> to
/// <c>false</c>, otherwise <see cref="TenantProvider"/> walks the plug-service branch, ignores
/// the supplied source, and returns an empty list.
/// </summary>
public class TenantSourceAuthoritativeTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>A detector that would happily supply tenants — present only to prove it is ignored.</summary>
    private sealed class FakeDetector(params int[] detected) : IOutboxTenantDetector
    {
        public bool CanDetect => true;
        public ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken) => ValueTask.FromResult(detected);
    }

    private static TenantProvider Provider(TenantSettings settings, IOutboxTenantDetector? detector = null) =>
        new(settings, detector);

    [Fact]
    public async Task WithTenantIdsDisablesAutoDetect()
    {
        var settings = new TenantSettings();
        settings.WithTenantIds(1, 2, 3);

        Assert.False(settings.AutoDetect);
    }

    [Fact]
    public async Task WithTenantSourceDisablesAutoDetect()
    {
        var settings = new TenantSettings();
        settings.WithTenantSource(new FakeDetector(42));

        Assert.False(settings.AutoDetect);
    }

    [Fact]
    public async Task WithTenantProviderDisablesAutoDetect()
    {
        var settings = new TenantSettings();
        settings.WithTenantProvider(_ => ValueTask.FromResult(new[] { 7 }));

        Assert.False(settings.AutoDetect);
    }

    /// <summary>
    /// The regression: with a detector present in the container, a static list must still win.
    /// If auto-detection were not disabled, <see cref="TenantProvider"/> would consult the detector
    /// and return the detected ids instead of the explicit ones.
    /// </summary>
    [Fact]
    public async Task WithTenantIdsWinsOverAPresentDetector()
    {
        var settings = new TenantSettings();
        settings.WithTenantIds(1, 2, 3);
        var provider = Provider(settings, new FakeDetector(999));

        var ids = await provider.GetTenantIds(TestToken);

        Assert.Equal(new[] { 1, 2, 3 }, ids);
    }

    /// <summary>
    /// A custom source must win over the plug-service detector too — the same branch bug, different
    /// source shape.
    /// </summary>
    [Fact]
    public async Task WithTenantSourceWinsOverAPresentDetector()
    {
        var source = new FakeDetector(999);
        var settings = new TenantSettings();
        settings.WithTenantSource(source);
        var provider = Provider(settings, new FakeDetector(1, 2, 3));

        var ids = await provider.GetTenantIds(TestToken);

        Assert.Equal(new[] { 999 }, ids);
    }

    /// <summary>
    /// A custom provider function must win over the plug-service detector as well.
    /// </summary>
    [Fact]
    public async Task WithTenantProviderWinsOverAPresentDetector()
    {
        var settings = new TenantSettings();
        settings.WithTenantProvider(_ => ValueTask.FromResult(new[] { 5, 6 }));
        var provider = Provider(settings, new FakeDetector(999));

        var ids = await provider.GetTenantIds(TestToken);

        Assert.Equal(new[] { 5, 6 }, ids);
    }

    /// <summary>
    /// Sanity: when auto-detection is genuinely enabled, the detector is consulted and its ids
    /// returned. Guards against the fix accidentally disabling detection for everyone.
    /// </summary>
    [Fact]
    public async Task AutoDetectStillConsultsTheDetectorWhenEnabled()
    {
        var settings = new TenantSettings();
        settings.WithAutoDetect();
        var provider = Provider(settings, new FakeDetector(11, 22));

        var ids = await provider.GetTenantIds(TestToken);

        Assert.Equal(new[] { 11, 22 }, ids);
    }

    /// <summary>
    /// When no detector is present and auto-detection is on, the plug branch returns an empty list
    /// (there is nothing to detect). This is the original silent-empty symptom, preserved as the
    /// documented behavior of the auto-detect branch.
    /// </summary>
    [Fact]
    public async Task AutoDetectWithoutDetectorReturnsEmpty()
    {
        var settings = new TenantSettings();
        settings.WithAutoDetect();
        var provider = Provider(settings);

        var ids = await provider.GetTenantIds(TestToken);

        Assert.Equal(Array.Empty<int>(), ids);
    }

    /// <summary>
    /// Regression guard: enabling auto-detection after an explicit source must fail fast rather
    /// than silently ignoring the source.
    /// </summary>
    [Theory]
    [InlineData(0)]
    public async Task WithAutoDetectAfterExplicitSourceThrows(int _)
    {
        var settings = new TenantSettings();
        settings.WithTenantIds(1, 2, 3);

        Assert.Throws<InvalidOperationException>(() => settings.WithAutoDetect());
    }

    [Fact]
    public async Task WithAutoDetectAfterTenantSourceThrows()
    {
        var settings = new TenantSettings();
        settings.WithTenantSource(new FakeDetector(1));

        Assert.Throws<InvalidOperationException>(() => settings.WithAutoDetect());
    }

    [Fact]
    public async Task WithAutoDetectAfterTenantProviderThrows()
    {
        var settings = new TenantSettings();
        settings.WithTenantProvider(_ => ValueTask.FromResult(new[] { 1 }));

        Assert.Throws<InvalidOperationException>(() => settings.WithAutoDetect());
    }

    [Fact]
    public async Task WithAutoDetectStaysOpenWhenNoSourceSet()
    {
        var settings = new TenantSettings();
        settings.WithAutoDetect();

        Assert.True(settings.AutoDetect);
    }
}
