using Microsoft.Extensions.Configuration;
using Sa.Data.PostgreSql;

namespace Sa.Configuration.PostgreSql;

/// <summary>
/// Configuration source backed by a PostgreSQL query.
/// </summary>
/// <remarks>
/// The source implements <see cref="ISaDynamicConfigurationSource"/>, so
/// <see cref="ISaConfigurationReloader.ReloadDynamicAsync"/> reloads it and nothing else. It also
/// owns the single <see cref="IPgDataSource"/> of the source: the post-processing pass rebuilds its
/// inner configuration from the same sources, and sharing the data source here is what keeps that
/// second provider from opening a second connection pool.
/// <para>
/// With <see cref="PgConfigurationConnection.FromServices(string?)"/> the data source is resolved
/// from dependency injection during the first runtime initialization, not when the source is
/// registered.
/// </para>
/// </remarks>
public sealed class DatabaseConfigurationSource : ISaDynamicConfigurationSource, IDisposable
{
    private readonly PgConfigurationConnection _connection;
    private readonly PostgreSqlConfigurationOptions _options;
    private readonly object _sync = new();

    private IPgDataSource? _dataSource;
    private int _builds;
    private int _initializerRegistered;
    private int _disposed;

    /// <summary>
    /// Creates the source with an explicit connection strategy.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The options or the connection string are invalid.</exception>
    public DatabaseConfigurationSource(PostgreSqlConfigurationOptions options, PgConfigurationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        connection.Validate();
        options.Validate();

        _options = options;
        _connection = connection;

        if (connection.Kind == PgConnectionKind.DataSource)
            _dataSource = connection.DataSourceValue;
    }

    /// <summary>
    /// Uses the application's data source from dependency injection — the default strategy.
    /// </summary>
    public DatabaseConfigurationSource(PostgreSqlConfigurationOptions options)
        : this(options, PgConfigurationConnection.FromServices())
    {
    }

    /// <summary>Uses a connection string owned by the source.</summary>
    public DatabaseConfigurationSource(PostgreSqlConfigurationOptions options, string connectionString)
        : this(options, PgConfigurationConnection.ConnectionString(connectionString))
    {
    }

    /// <summary>Uses a data source owned by the caller.</summary>
    public DatabaseConfigurationSource(PostgreSqlConfigurationOptions options, IPgDataSource dataSource)
        : this(options, PgConfigurationConnection.DataSource(dataSource))
    {
    }

    /// <summary>The connection strategy this source was created with.</summary>
    public PgConfigurationConnection Connection => _connection;

    /// <summary>The query (and client mapping) this source reads.</summary>
    public PostgreSqlConfigurationOptions Options => _options;

    /// <inheritdoc/>
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        SaConfigurationRuntime runtime = SaConfigurationRuntime.GetOrAdd(builder);

        // The first build belongs to the application's own configuration; a later build comes from
        // the post-processing pass rebuilding its inner view. Only the first one is registered as a
        // dynamic provider, so a reload does not query the database twice for one layer.
        bool primary = Interlocked.Increment(ref _builds) == 1;
        DatabaseConfigurationProvider provider = new(this, _options, primary);

        if (primary)
        {
            runtime.RegisterDynamicProvider(provider);

            if (_connection.Kind == PgConnectionKind.FromServices)
                RegisterInitializer(runtime);
        }

        return provider;
    }

    /// <summary>
    /// Disposes the data source this source created (the <see cref="PgConnectionKind.ConnectionString"/>
    /// strategy). A source passed in, or resolved from dependency injection, is left alone.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        if (_connection.Kind != PgConnectionKind.ConnectionString)
            return;

        IPgDataSource? owned;

        lock (_sync)
        {
            owned = _dataSource;
            _dataSource = null;
        }

        owned?.Dispose();
    }

    /// <summary>
    /// Returns the data source to read through, creating the owned one on first use.
    /// Returns <see langword="null"/> while a late-bound source has not been resolved yet.
    /// </summary>
    internal IPgDataSource? GetDataSource()
    {
        IPgDataSource? existing = Volatile.Read(ref _dataSource);
        if (existing is not null)
            return existing;

        if (_connection.Kind != PgConnectionKind.ConnectionString)
            return null;

        lock (_sync)
        {
            if (_dataSource is not null)
                return _dataSource;

            if (Volatile.Read(ref _disposed) == 1)
                return null;

            _dataSource = IPgDataSource.Create(_connection.ConnectionStringValue!);
            return _dataSource;
        }
    }

    internal void SetDataSource(IPgDataSource dataSource)
    {
        lock (_sync)
        {
            _dataSource ??= dataSource;
        }
    }

    private void RegisterInitializer(SaConfigurationRuntime runtime)
    {
        if (Interlocked.Exchange(ref _initializerRegistered, 1) == 1)
            return;

        runtime.RegisterInitializer((services, _) =>
        {
            if (GetDataSource() is not null)
                return ValueTask.CompletedTask;

            IPgDataSource resolved;

            try
            {
                resolved = PgDataSourceResolver.Resolve(services, _connection.ServiceKey);
            }
            catch (InvalidOperationException) when (_connection.Optional)
            {
                return ValueTask.CompletedTask;
            }

            SetDataSource(resolved);

            return ValueTask.CompletedTask;
        });
    }
}
