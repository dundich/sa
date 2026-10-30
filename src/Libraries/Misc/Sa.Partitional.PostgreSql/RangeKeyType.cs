namespace Sa.Partitional.PostgreSql;

/// <summary>
/// The type of the column used for range partitioning.
/// </summary>
/// <remarks>
/// The library generates range bounds for the column; the type decides how a bound is written in
/// DDL. <see cref="UnixSeconds"/> is the historical <c>bigint</c> mode and stays the default for a
/// table whose range column is not declared (the library then adds a <c>bigint</c> column itself).
/// The partition cache still stores <c>from_date</c>/<c>to_date</c> as Unix seconds in every mode.
/// </remarks>
public enum RangeKeyType
{
    /// <summary>
    /// A <c>bigint</c> holding Unix seconds (the default). Bounds are emitted as integers.
    /// </summary>
    UnixSeconds = 0,

    /// <summary>
    /// A <c>timestamp with time zone</c> / <c>timestamptz</c> column. Bounds are emitted as
    /// UTC timestamps (<c>'2026-10-29 00:00:00+00'</c>).
    /// </summary>
    TimestampTz = 1,

    /// <summary>
    /// A <c>timestamp without time zone</c> column. Values are treated as UTC; bounds are emitted
    /// without an offset (<c>'2026-10-29 00:00:00'</c>).
    /// </summary>
    Timestamp = 2,

    /// <summary>
    /// A <c>date</c> column. Bounds are emitted as dates (<c>'2026-10-29'</c>).
    /// </summary>
    Date = 3,
}
