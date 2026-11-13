namespace Sa.Configuration.Clients;

/// <summary>
/// A registered provider callback: resolves its dependencies from the application's container and
/// returns the client settings it knows about.
/// </summary>
/// <param name="services">The application's service provider.</param>
/// <param name="cancellationToken">Cancellation token.</param>
internal delegate ValueTask<IReadOnlyList<ClientSetting>> RowLoader(
    IServiceProvider services,
    CancellationToken cancellationToken);
