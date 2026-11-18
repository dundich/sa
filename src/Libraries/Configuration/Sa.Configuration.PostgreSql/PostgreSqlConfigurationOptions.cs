
using Npgsql;

namespace Sa.Configuration.PostgreSql;
/// <summary>
/// Options for the PostgreSQL-backed configuration source.
/// </summary>
/// <remarks>
/// The connection itself is not part of the options: pass it with
/// <see cref="PgConfigurationConnection"/> (<c>ConnectionString</c>, <c>DataSource</c> or
/// <c>FromServices</c>). This keeps "where the data comes from" and "what to read" independent.
/// </remarks>
public sealed record PostgreSqlConfigurationOptions
{
    /// <summary>
    /// Query returning exactly two columns, <c>(key, value)</c>. For a client overlay, read the rows
    /// with a client provider (filter by <c>client_id</c>) rather than mapping a prefix here.
    /// </summary>
    public required string SelectSql { get; init; }

    /// <summary>Named parameters for <see cref="SelectSql"/>. Cloned per load.</summary>
    public IReadOnlyCollection<NpgsqlParameter> Parameters { get; init; } = [];

    /// <summary>Returns a copy carrying <paramref name="parameters"/> for <see cref="SelectSql"/>.</summary>
    /// <param name="parameters">Named parameters, in any order; each is cloned per load.</param>
    public PostgreSqlConfigurationOptions WithParameters(params NpgsqlParameter[] parameters)
        => this with { Parameters = parameters };

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
         + $"{nameof(LastWins)} = {LastWins} }}";

    /// <summary>
    /// Fails fast at registration time, so a misconfigured source fails while the host is
    /// still building rather than inside the first — retried — load at runtime.
    /// </summary>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SelectSql);

        ArgumentOutOfRangeException.ThrowIfNegative(MaxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegative(MedianFirstRetryDelay);
    }
}
