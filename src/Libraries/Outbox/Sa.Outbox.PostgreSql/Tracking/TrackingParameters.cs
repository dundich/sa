using Npgsql;
using NpgsqlTypes;

namespace Sa.Outbox.PostgreSql.Tracking;

/// <summary>Parameter helpers shared by the tracking and maintenance implementations.</summary>
internal static class TrackingParameters
{
    /// <summary>
    /// Binds an optional text filter. The type must be explicit, otherwise Npgsql cannot bind the NULL
    /// that <c>(@gr IS NULL OR column = @gr)</c> reads.
    /// </summary>
    public static void AddOptionalText(NpgsqlCommand command, string name, string? value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value ?? (object)DBNull.Value,
        });
}
