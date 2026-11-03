namespace Sa.Schedule.Cron;

/// <summary>
/// Thrown when a cron expression cannot be parsed.
/// Derives from <see cref="FormatException"/>, so callers that already handle a bad
/// expression format keep working.
/// </summary>
public class CronParseException : FormatException
{
    /// <summary>
    /// The field that failed to parse ("minute", "day-of-month", ...), or "expression"
    /// when the whole expression is at fault. Null when not supplied.
    /// </summary>
    public string? Field { get; }

    /// <summary>
    /// The offending token. Null when not supplied.
    /// </summary>
    public string? Token { get; }

    /// <summary>
    /// Creates an exception with a message only.
    /// </summary>
    public CronParseException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception naming the field and the token that failed.
    /// </summary>
    public CronParseException(string message, string field, string token)
        : base(message)
    {
        Field = field;
        Token = token;
    }

    /// <summary>
    /// Creates an exception naming the field and the token that failed, wrapping an inner failure.
    /// </summary>
    public CronParseException(string message, string field, string token, Exception innerException)
        : base(message, innerException)
    {
        Field = field;
        Token = token;
    }
}
