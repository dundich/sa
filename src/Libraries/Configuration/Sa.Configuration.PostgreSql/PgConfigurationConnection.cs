using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Configuration.PostgreSql;

/// <summary>How the configuration source obtains its database connection.</summary>
public enum PgConnectionKind
{
    /// <summary>A connection string owned (and pooled) by the configuration source.</summary>
    ConnectionString,

    /// <summary>A data source passed in by the application; the caller keeps ownership.</summary>
    DataSource,

    /// <summary>The application's data source, resolved from dependency injection on first use.</summary>
    FromServices
}

/// <summary>
/// Connection strategy of the PostgreSQL configuration source.
/// </summary>
/// <remarks>
/// Three ways to say "which database", matching how the rest of <c>Sa.Data.PostgreSql</c> works:
/// <list type="bullet">
/// <item><see cref="ConnectionString(string)"/> — the source owns a private pool;</item>
/// <item><see cref="DataSource(IPgDataSource)"/> — share a pool created by the application;</item>
/// <item><see cref="FromServices(string?)"/> — take the application's already registered data
/// source (<c>IPgDataSource</c> or <c>NpgsqlDataSource</c>, keyed or default), exactly like
/// <see cref="PgDataSourceResolver"/>. This is the default when no strategy is given.</item>
/// </list>
/// <para>
/// <see cref="FromServices(string?)"/> is late-bound: configuration is assembled before the
/// container exists, so the data source is resolved when the runtime is initialized
/// (<c>ISaConfigurationReloader.EnsureInitializedAsync</c>, or the hosted hook). Until then the
/// database layer contributes no keys.
/// </para>
/// </remarks>
public sealed record PgConfigurationConnection
{
    private PgConfigurationConnection()
    {
    }

    internal PgConnectionKind Kind { get; private init; }

    internal string? ConnectionStringValue { get; private init; }

    internal string? ServiceKey { get; private init; }

    internal IPgDataSource? DataSourceValue { get; private init; }

    /// <summary>
    /// When <see langword="true"/>, an unresolvable <see cref="FromServices(string?)"/> source is
    /// treated as "not configured": the database layer stays empty and a warning is logged, instead
    /// of failing the start. Default is <see langword="false"/> (fail fast).
    /// </summary>
    public bool Optional { get; init; }

    /// <summary>
    /// Uses a connection string; the configuration source creates and owns the connection pool.
    /// </summary>
    /// <exception cref="ArgumentException">The value is blank or is not a valid connection string.</exception>
    public static PgConfigurationConnection ConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new ArgumentException(
                "The PostgreSQL connection string is not valid; see the inner exception.", nameof(connectionString), exception);
        }

        return new PgConfigurationConnection
        {
            Kind = PgConnectionKind.ConnectionString,
            ConnectionStringValue = connectionString
        };
    }

    /// <summary>
    /// Uses an existing <see cref="IPgDataSource"/>; the caller keeps ownership and the pool is
    /// shared with the rest of the application.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is null.</exception>
    public static PgConfigurationConnection DataSource(IPgDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        return new PgConfigurationConnection
        {
            Kind = PgConnectionKind.DataSource,
            DataSourceValue = dataSource
        };
    }

    /// <summary>
    /// Takes the data source registered in dependency injection — the same resolution order as
    /// <see cref="PgDataSourceResolver"/>: keyed <see cref="IPgDataSource"/>, the default
    /// <see cref="IPgDataSource"/>, then a registered <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <param name="serviceKey">
    /// Key of the data source, or <see langword="null"/>/empty for the default one. A missing keyed
    /// source falls back to the default one, exactly as the resolver does.
    /// </param>
    public static PgConfigurationConnection FromServices(string? serviceKey = null)
        => new()
        {
            Kind = PgConnectionKind.FromServices,
            ServiceKey = string.IsNullOrEmpty(serviceKey) ? null : serviceKey
        };

    /// <summary>
    /// Returns a copy that does not fail the start when the data source cannot be resolved.
    /// </summary>
    public PgConfigurationConnection AsOptional() => this with { Optional = true };

    /// <summary>
    /// Redacts the password of a connection string: the object routinely ends up in a log line
    /// through record-generated formatting, and a connection string carries the password.
    /// </summary>
    public override string ToString()
    {
        string target = Kind switch
        {
            PgConnectionKind.ConnectionString => Redact(ConnectionStringValue),
            PgConnectionKind.DataSource => nameof(IPgDataSource),
            _ => ServiceKey is null
                ? $"{nameof(IPgDataSource)} from services"
                : $"{nameof(IPgDataSource)} from services (key '{ServiceKey}')"
        };

        return $"{nameof(PgConfigurationConnection)} {{ {nameof(Kind)} = {Kind}, Target = {target}, {nameof(Optional)} = {Optional} }}";
    }

    private static string Redact(string? connectionString)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString) { Password = "***" }.ConnectionString;
        }
        catch
        {
            // A malformed connection string is exactly the case where echoing it verbatim is most
            // dangerous, so it is never rendered.
            return "<unparseable>";
        }
    }

    internal void Validate()
    {
        switch (Kind)
        {
            case PgConnectionKind.ConnectionString:
                ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionStringValue);
                break;

            case PgConnectionKind.DataSource:
                ArgumentNullException.ThrowIfNull(DataSourceValue);
                break;

            default:
                break;
        }
    }
}
