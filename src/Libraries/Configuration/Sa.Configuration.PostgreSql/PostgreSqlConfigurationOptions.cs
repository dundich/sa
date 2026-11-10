
using Npgsql;

namespace Sa.Configuration.PostgreSql;
/// <summary>
/// Options for the PostgreSQL-backed configuration source.
/// </summary>
/// <param name="SelectSql">
/// Query returning two columns, <c>(key, value)</c>, or three columns, <c>(client, key, value)</c>
/// when <see cref="ClientColumn"/> is set.
/// </param>
/// <param name="Parameters">Named parameters for <paramref name="SelectSql"/>. Cloned per load.</param>
/// <remarks>
/// The connection itself is not part of the options: pass it with
/// <see cref="PgConfigurationConnection"/> (<c>ConnectionString</c>, <c>DataSource</c> or
/// <c>FromServices</c>). This keeps "where the data comes from" and "what to read" independent.
/// </remarks>
public sealed record PostgreSqlConfigurationOptions(
    string SelectSql,
    params IReadOnlyCollection<NpgsqlParameter> Parameters)
{
    /// <summary>
    /// Total load attempts, including the first. Defaults to 3. Only transient Npgsql failures
    /// (network errors, <c>08xxx</c> SQLSTATEs and similar) are retried; a permanent error fails on
    /// the first attempt regardless of this value. <c>0</c> and <c>1</c> both mean a single attempt.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Median delay of the first retry in milliseconds; each delay is sampled around it
    /// (decorrelated jitter). Defaults to 530. The first retry always fires immediately.
    /// </summary>
    public int MedianFirstRetryDelay { get; init; } = 530;

    /// <summary>
    /// When <see langword="true"/> (default), a row whose value is <see langword="null"/>, empty
    /// or whitespace-only is skipped — which makes this source <em>delegate</em> such keys to
    /// lower-priority providers. Set to <see langword="false"/> to store them, so this source
    /// <em>overrides</em> lower-priority providers instead.
    /// </summary>
    /// <remarks>
    /// This governs the value only. A blank or whitespace-only <em>key</em> is always dropped,
    /// whatever this is set to: an empty key is not a meaningful path in <c>IConfiguration</c>,
    /// and <c>GetChildKeys</c> misbehaves on one.
    /// </remarks>
    public bool SkipEmptyValues { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/>, a double underscore in a key is read as the
    /// <c>:</c> configuration separator, so <c>sa__pg__host</c> in the database addresses the
    /// same setting as <c>sa:pg:host</c> — the convention <c>AddEnvironmentVariables()</c>
    /// already applies to environment variables. Off by default: keys are stored verbatim, so
    /// a key that legitimately contains <c>__</c> is not silently re-shaped.
    /// </summary>
    /// <remarks>
    /// When on, keys are normalized after trimming and before duplicate resolution, so if
    /// the query returns both <c>sa__pg__host</c> and <c>sa:pg:host</c>, <see cref="LastWins"/>
    /// decides which value survives, and a request for either spelling answers for the same
    /// setting.
    /// </remarks>
    public bool DoubleUnderscoreAsSeparator { get; init; }

    /// <summary>
    /// When <see langword="true"/> (default), the last row wins for duplicated keys. When
    /// <see langword="false"/>, the first row wins.
    /// </summary>
    public bool LastWins { get; init; } = true;

    /// <summary>
    /// Constant client name. When set, every key returned by the query is stored under
    /// <c>{ClientsSection}:{Client}:{key}</c>, so the source contributes a client overlay instead of
    /// base values. Use it when the query is already filtered by client
    /// (<c>WHERE client_id = @client_id</c>) and you want that source to be readable through
    /// <c>IConfiguration</c> with a client key.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="ClientColumn"/>. When both are absent, keys are stored
    /// verbatim — the normal base layer.
    /// </remarks>
    public string? Client { get; init; }

    /// <summary>
    /// Column that carries the client name, for a query that returns all clients at once as
    /// <c>(client, key, value)</c>. Rows with a <see langword="null"/> or empty client are stored
    /// as base values; the rest go under <c>{ClientsSection}:{client}:{key}</c>, which is what makes
    /// several clients coexist in one configuration.
    /// </summary>
    /// <remarks>
    /// A single query is the only shape that supports clients appearing at runtime: a per-client
    /// filtered query would need one source (and one pool) per client and could not see a new one
    /// before a restart.
    /// </remarks>
    public string? ClientColumn { get; init; }

    /// <summary>
    /// Section that holds per-client overrides. Defaults to <c>sa:clients</c>, the value the rest of
    /// Sa uses.
    /// </summary>
    public string ClientsSection { get; init; } = "sa:clients";

    /// <summary>
    /// Overridden because the compiler-generated record <c>ToString</c> would inline
    /// <see cref="Parameters"/> — and therefore their values, which routinely carry connection
    /// secrets — into every log line and exception message.
    /// </summary>
    public override string ToString()
        => $"{nameof(PostgreSqlConfigurationOptions)} {{ "
         + $"{nameof(SelectSql)} = {SelectSql}, "
         + $"{nameof(Parameters)} = [{Parameters?.Count ?? 0} item(s)], "
         + $"{nameof(MaxAttempts)} = {MaxAttempts}, "
         + $"{nameof(MedianFirstRetryDelay)} = {MedianFirstRetryDelay}, "
         + $"{nameof(SkipEmptyValues)} = {SkipEmptyValues}, "
         + $"{nameof(DoubleUnderscoreAsSeparator)} = {DoubleUnderscoreAsSeparator}, "
         + $"{nameof(LastWins)} = {LastWins}, "
         + $"{nameof(Client)} = {Client ?? "<none>"}, "
         + $"{nameof(ClientColumn)} = {ClientColumn ?? "<none>"}, "
         + $"{nameof(ClientsSection)} = {ClientsSection} }}";

    /// <summary>
    /// Fails fast at registration time, so a misconfigured source fails while the host is
    /// still building rather than inside the first — retried — load at runtime.
    /// </summary>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SelectSql);

        ArgumentOutOfRangeException.ThrowIfNegative(MaxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegative(MedianFirstRetryDelay);

        ArgumentException.ThrowIfNullOrWhiteSpace(ClientsSection);

        if (Client is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(Client);

        if (ClientColumn is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(ClientColumn);

        if (Client is not null && ClientColumn is not null)
        {
            throw new ArgumentException(
                $"{nameof(Client)} and {nameof(ClientColumn)} are mutually exclusive: "
                + "use Client for a per-client filtered query, or ClientColumn for one query that "
                + "returns every client.");
        }
    }
}
