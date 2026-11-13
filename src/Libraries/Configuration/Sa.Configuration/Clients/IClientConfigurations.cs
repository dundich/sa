using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Sa.Configuration.Clients;

/// <summary>
/// Registry of per-client configuration overlays.
/// </summary>
/// <remarks>
/// The core configuration keeps the shared values; every client has its own, separate
/// <see cref="IConfiguration"/> overlay with the <b>same key names</b> and no prefix. Reading a
/// client view resolves the client's value first and falls back to the core value, so
/// <c>core["s3_url"]</c> and <c>clients.GetClient("acme")["s3_url"]</c> are different values of the
/// same field.
/// <para>
/// Providers registered with
/// <see cref="SaClientConfigurationRegistration.Add{T1}(Func{T1, CancellationToken, ValueTask{IReadOnlyList{ClientSetting}}})"/>
/// return flat <see cref="ClientSetting"/> rows, which the registry groups by client. They are read
/// on the first access and again on <see cref="ReloadAsync"/>, so a client discovered later becomes
/// available without a restart — no configuration rebuild. <see cref="AttachAsync"/> adds an
/// overlay from the side at runtime.
/// </para>
/// <para>
/// A client's rows are rewritten by the same transform chain the core pass runs: <c>{{placeholder}}</c>
/// secrets are substituted and key aliases are resolved inside the client's own values, so a client
/// can keep its settings under the flat names its own system uses. Alias sources are read from the
/// client first and from the core second, and a derived value that only mirrors the core is not
/// copied into the overlay — the client keeps reading the core live. Keys are stored in the canonical
/// <c>:</c> spelling, so <c>sa__pg__host</c> and <c>sa:pg:host</c> are one setting.
/// </para>
/// <para>
/// Reading <see cref="Clients"/> or <see cref="GetClient"/> for the first time loads the providers
/// synchronously (the configuration API has no asynchronous accessor), so a provider that does I/O
/// should be quick or pre-warmed with <see cref="ReloadAsync"/> at startup.
/// </para>
/// </remarks>
public interface IClientConfigurations
{
    /// <summary>
    /// Clients currently known: the ids reported by the providers plus the runtime attachments.
    /// Reading this property loads the providers if they have not been read yet.
    /// </summary>
    IReadOnlyCollection<string> Clients { get; }

    /// <summary>
    /// Returns the live view of <paramref name="client"/> — its overlay over the core
    /// configuration. An unknown client reads the core values unless
    /// <see cref="ClientConfigurationOptions.ThrowForUnknownClient"/> is set.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="client"/> is blank.</exception>
    IConfiguration GetClient(string client);

    /// <summary>
    /// Resolves the view of <paramref name="client"/> and reports whether the client is known.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetClient"/>, this never throws for an unknown client — not even when
    /// <see cref="ClientConfigurationOptions.ThrowForUnknownClient"/> is set. The returned view
    /// reads the core values in that case.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="client"/> is blank.</exception>
    bool TryGet(string client, out IConfiguration configuration);

    /// <summary>
    /// Attaches an overlay built from <paramref name="source"/> to <paramref name="client"/>.
    /// </summary>
    /// <remarks>
    /// Intended for runtime registration (a plugin, a manually configured client, a test), and it
    /// wins over anything a provider reports for the same client. An existing overlay for the same
    /// client is replaced.
    /// </remarks>
    Task AttachAsync(
        string client,
        IConfigurationSource source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an overlay attached with <see cref="AttachAsync"/> and disposes it. Returns whether
    /// such an overlay existed.
    /// </summary>
    /// <remarks>
    /// A client that a provider still reports cannot be removed: the next access rebuilds its
    /// overlay from the rows, so the call returns <see langword="false"/> and the view keeps its
    /// values. The fallback to the core values only happens for a client no provider reports.
    /// </remarks>
    Task<bool> RemoveAsync(string client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads every provider and refreshes the overlays created so far, adding new clients and
    /// dropping the removed ones.
    /// </summary>
    ValueTask ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised when a client is attached, removed or reloaded.</summary>
    IChangeToken GetReloadToken();
}
