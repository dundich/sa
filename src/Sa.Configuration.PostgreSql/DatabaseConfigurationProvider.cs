using Microsoft.Extensions.Configuration;
using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Configuration.PostgreSql;


/// <summary>
/// Configuration provider that loads settings from a PostgreSQL database.
/// </summary>
public sealed class DatabaseConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly PostgreSqlConfigurationOptions _options;
    private readonly IPgDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly NpgsqlParameter[] _parameters;

    /// <summary>
    /// Re-entrancy guard: 0 = idle, 1 = a load is in flight. Guards the load itself, not the
    /// dictionary swap — assigning <see cref="ConfigurationProvider.Data"/> is a single
    /// reference store, and <see cref="ConfigurationProvider.TryGet"/> reads it without a lock,
    /// so locking around the assignment would cost without ever making readers safer.
    /// </summary>
    private int _loading;

    /// <summary>Makes <see cref="Dispose"/> idempotent.</summary>
    private int _disposed;


    /// <summary>
    /// Creates a provider owning a private data source (and therefore its connection pool),
    /// built once and reused across every load — <see cref="Load"/> runs on each
    /// <see cref="IConfigurationRoot.Reload"/>.
    /// </summary>
    public DatabaseConfigurationProvider(PostgreSqlConfigurationOptions options)
        : this(Validate(options), ownsDataSource: true)
    {
    }

    /// <summary>
    /// Validated <paramref name="options"/> in, builds the owned data source out of it. Split out
    /// so the connection string is validated exactly once before it reaches
    /// <see cref="IPgDataSource.Create"/>.
    /// </summary>
    private DatabaseConfigurationProvider(PostgreSqlConfigurationOptions options, bool ownsDataSource)
        : this(options, IPgDataSource.Create(options.ConnectionString), ownsDataSource)
    {
    }

    /// <summary>
    /// Creates a provider over an existing data source — typically the DI-registered
    /// <see cref="IPgDataSource"/>, so the configuration source shares the application's
    /// connection pool instead of owning a second one. The caller keeps ownership.
    /// </summary>
    internal DatabaseConfigurationProvider(
        PostgreSqlConfigurationOptions options,
        IPgDataSource dataSource,
        bool ownsDataSource = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataSource);

        _options = options;
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _parameters = [.. options.Parameters.Select(static p => (NpgsqlParameter)p.Clone())];
    }


    /// <summary>
    /// Loads configuration from PostgreSQL. Invoked by <see cref="ConfigurationProvider"/>
    /// on build and on every <see cref="IConfigurationRoot.Reload"/>; the base class offers no
    /// async contract here, so this blocks on <see cref="LoadAsync"/>.
    /// </summary>
    public override void Load()
    {
        if (!TryBeginLoad())
            return;

        try
        {
            LoadCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            EndLoad();
        }
    }

    /// <summary>
    /// Asynchronously loads configuration, and signals the reload token so that consumers
    /// holding the provider directly (rather than the <see cref="IConfigurationRoot"/>, which
    /// raises its own token) observe the new snapshot.
    /// </summary>
    /// <remarks>
    /// Returns without reloading when a load is already in flight, so that concurrent reloads
    /// collapse into one query instead of racing.
    /// </remarks>
    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginLoad())
            return;

        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            OnReload();
        }
        finally
        {
            EndLoad();
        }
    }

    private bool TryBeginLoad() => Interlocked.Exchange(ref _loading, 1) == 0;

    private void EndLoad() => Volatile.Write(ref _loading, 0);

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        // Built outside the retry loop and swapped in only on success, so a failed reload
        // leaves the previous snapshot serving reads instead of wiping the configuration.
        Dictionary<string, string?> newData = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            // The retry loop must see the original NpgsqlException: shouldRetry tests
            // `ex is NpgsqlException`, and Retry.WaitAndRetry hands the predicate the
            // escaping exception without unwrapping InnerException. Wrapping here — inside
            // the retried delegate — is what silently disabled retries before.
            await PgRetryStrategy
                .ExecuteWithRetry(
                    fun: async ct => await ReadAsync(newData, ct).ConfigureAwait(false),
                    retryCount: _options.MaxAttempts,
                    medianFirstRetryDelay: _options.MedianFirstRetryDelay,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Never wrapped: cancellation has to stay observable as cancellation, or it
            // turns into a retriable failure and outlives the shutdown it was requested for.
            throw;
        }
        catch (InvalidOperationException)
        {
            // Already a precise, self-describing error (e.g. a malformed query shape) —
            // re-wrapping it would only bury the message the user needs to see.
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load configuration from PostgreSQL. Query: {_options.SelectSql}", ex);
        }

        Data = newData;
    }

    private async Task<int> ReadAsync(Dictionary<string, string?> newData, CancellationToken cancellationToken)
    {
        return await _dataSource
            .ExecuteReader(
                _options.SelectSql,
                (reader, index) =>
                {
                    if (index == 0 && reader.FieldCount < 2)
                    {
                        throw new InvalidOperationException(
                            $"The configuration query must return two columns (key, value), but it returned {reader.FieldCount}.");
                    }

                    if (reader.IsDBNull(0))
                        return;

                    string key = reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    string? value = reader.IsDBNull(1) ? null : reader.GetString(1);

                    if (_options.SkipEmptyValues && string.IsNullOrWhiteSpace(value))
                        return;

                    // Trim() returns the same instance when there is nothing to trim.
                    if (_options.LastWins)
                        newData[key.Trim()] = value?.Trim();
                    else
                        newData.TryAdd(key.Trim(), value?.Trim());
                },
                cmd =>
                {
                    // The command is created per call, so there is nothing to clear; the
                    // parameters are cloned because the same options instance is reused
                    // across loads and a parameter cannot be attached twice.
                    if (_parameters.Length == 0)
                        return;

                    foreach (NpgsqlParameter parameter in _parameters)
                        cmd.Parameters.Add(parameter.Clone());
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the owned data source — and with it the connection pool — if this provider owns
    /// it. A data source passed in from the outside is left alone; its owner disposes it.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigurationProvider"/> is no longer <see cref="IDisposable"/> in .NET 10, so
    /// this is implemented directly; <see cref="IConfigurationRoot"/> disposes the providers it
    /// owns, so <see cref="ConfigurationRoot.Dispose"/> reaches it.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        if (_ownsDataSource)
            _dataSource.Dispose();
    }

    private static PostgreSqlConfigurationOptions Validate(PostgreSqlConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }
}
