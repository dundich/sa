using Microsoft.Extensions.Configuration;
using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Configuration.PostgreSql;

/// <summary>
/// Configuration provider that loads settings from a PostgreSQL database.
/// </summary>
/// <remarks>
/// The provider owns no connection: it reads through the <see cref="DatabaseConfigurationSource"/>
/// data source, which the source creates once (connection string), receives once (passed-in source)
/// or resolves once from dependency injection (late-bound source). That is what lets the
/// post-processing pass build a second provider over the same source without a second pool.
/// </remarks>
public sealed class DatabaseConfigurationProvider : ConfigurationProvider, ISaAsyncConfigurationProvider, ISaReloadableConfigurationProvider, IDisposable
{
    private readonly DatabaseConfigurationSource _source;
    private readonly PostgreSqlConfigurationOptions _options;
    private readonly bool _ownsSource;
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

    internal DatabaseConfigurationProvider(
        DatabaseConfigurationSource source,
        PostgreSqlConfigurationOptions options,
        bool ownsSource)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        _source = source;
        _options = options;
        _ownsSource = ownsSource;
        _parameters = [.. options.Parameters.Select(static p => (NpgsqlParameter)p.Clone())];
    }

    /// <summary>
    /// Loads configuration from PostgreSQL. Invoked by <see cref="ConfigurationProvider"/>
    /// on build and on every <see cref="IConfigurationRoot.Reload"/>; the base class offers no
    /// async contract here, so this blocks on <see cref="LoadAsync"/>.
    /// </summary>
    /// <remarks>
    /// Does nothing while a late-bound connection has not been resolved yet: the source contributes
    /// no keys until the runtime initializes it, and reading an empty layer is preferable to failing
    /// a host that never asked for the database.
    /// </remarks>
    public override void Load()
    {
        if (_source.GetDataSource() is null)
            return;

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
        if (_source.GetDataSource() is null)
            return;

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

    /// <summary>
    /// Synchronously reloads the provider and raises its reload token.
    /// </summary>
    /// <remarks>Used by <see cref="ISaConfigurationReloader"/> when it drives the refresh itself.</remarks>
    public void Reload()
    {
        Load();
        OnReload();
    }

    /// <summary>
    /// Answers for both spellings of a key: when <see cref="PostgreSqlConfigurationOptions.DoubleUnderscoreAsSeparator"/>
    /// is on, <c>configuration["sa__pg__host"]</c> finds the value stored as <c>sa:pg:host</c>.
    /// The alias already applies to keys read from the database; normalizing the requested key
    /// as well makes the two spellings truly interchangeable for consumers.
    /// </summary>
    public override bool TryGet(string key, out string? value)
        => base.TryGet(_options.DoubleUnderscoreAsSeparator ? ConfigurationKey.Normalize(key) : key, out value);

    /// <summary>
    /// Releases the source — and with it the connection pool — when this provider was the one that
    /// created the source's data source. A data source passed in or resolved from dependency
    /// injection is left alone; its owner disposes it.
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

        if (_ownsSource)
            _source.Dispose();
    }

    private bool TryBeginLoad() => Interlocked.Exchange(ref _loading, 1) == 0;
    private void EndLoad() => Volatile.Write(ref _loading, 0);

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        IPgDataSource dataSource = _source.GetDataSource()
            ?? throw new InvalidOperationException(
                "The PostgreSQL configuration data source is not available. "
                + "Use the ConnectionString/DataSource strategy, or initialize the late-bound source "
                + "with ISaConfigurationReloader.EnsureInitializedAsync after the host is built.");

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
                    fun: async ct =>
                    {
                        // A failed attempt must not leak its partial rows into the retry.
                        newData.Clear();

                        return await ReadAsync(dataSource, newData, ct).ConfigureAwait(false);
                    },
                    // MaxAttempts already counts the first attempt: the retry sequence yields
                    // exactly MaxAttempts delays (the first one is a zero-delay "fast first"),
                    // so the helper runs exactly MaxAttempts attempts.
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

    private async Task<int> ReadAsync(
        IPgDataSource dataSource,
        Dictionary<string, string?> newData,
        CancellationToken cancellationToken)
    {
        string? clientColumn = _options.ClientColumn;
        int clientOrdinal = -1;

        return await dataSource
            .ExecuteReader(
                _options.SelectSql,
                (reader, index) =>
                {
                    if (index == 0)
                    {
                        int required = clientColumn is null ? 2 : 3;

                        if (reader.FieldCount < required)
                        {
                            throw new InvalidOperationException(
                                clientColumn is null
                                    ? $"The configuration query must return two columns (key, value), but it returned {reader.FieldCount}."
                                    : $"The configuration query must return three columns (client, key, value) when {nameof(PostgreSqlConfigurationOptions.ClientColumn)} is set, but it returned {reader.FieldCount}.");
                        }

                        if (clientColumn is not null)
                        {
                            clientOrdinal = FindOrdinal(reader, clientColumn);

                            if (clientOrdinal < 0)
                            {
                                throw new InvalidOperationException(
                                    $"The configuration query does not return the client column '{clientColumn}'.");
                            }

                            if (clientOrdinal > 0)
                            {
                                throw new InvalidOperationException(
                                    $"The client column must be the first column of the configuration query, but '{clientColumn}' is at position {clientOrdinal}.");
                            }
                        }
                    }

                    int keyOrdinal = clientColumn is null ? 0 : 1;
                    int valueOrdinal = clientColumn is null ? 1 : 2;

                    if (reader.IsDBNull(keyOrdinal))
                        return;

                    // Trim() returns the same instance when there is nothing to trim.
                    string key = reader.GetString(keyOrdinal).Trim();
                    if (key.Length == 0)
                        return;

                    // `sa__pg__host` in the database addresses the same setting as
                    // `sa:pg:host`, matching how AddEnvironmentVariables() treats `__`.
                    if (_options.DoubleUnderscoreAsSeparator)
                        key = ConfigurationKey.Normalize(key);

                    string? client = _options.Client;

                    if (clientOrdinal >= 0)
                    {
                        client = reader.IsDBNull(clientOrdinal) ? null : reader.GetString(clientOrdinal).Trim();

                        if (client is { Length: 0 })
                            client = null;
                    }

                    if (client is not null)
                        key = $"{_options.ClientsSection}:{client}:{key}";

                    string? value = reader.IsDBNull(valueOrdinal) ? null : reader.GetString(valueOrdinal);

                    if (_options.SkipEmptyValues && string.IsNullOrWhiteSpace(value))
                        return;

                    if (_options.LastWins)
                        newData[key] = value?.Trim();
                    else
                        newData.TryAdd(key, value?.Trim());
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

    private static int FindOrdinal(NpgsqlDataReader reader, string column)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), column, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
