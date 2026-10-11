namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Cross-registration uniqueness of the physical file table: one table belongs to exactly one keyed
/// PostgreSQL file storage.
/// </summary>
/// <remarks>
/// Each keyed registration owns its partitional module, its migration schedule and its cleanup
/// schedule. Two registrations declaring the same table would therefore run two sets of migration
/// and retention jobs over one physical table — each free to drop partitions the other still needs.
/// The registry makes that a startup error instead.
/// <para>
/// The check runs through the standard options pipeline (see
/// <see cref="PostgresFileStorageOptionsValidator"/>): the shared validator claims the table for the
/// registration's options-instance name — which is the registration key — the first time the options
/// materialise (first read, or host start via <c>ValidateOnStart()</c>). It is idempotent for a name
/// that re-validates with the same table.
/// </para>
/// <para>
/// The claim is keyed on <see cref="PostgresFileStorageOptions.TableName"/> <b>alone</b>, not on the
/// schema: two registrations with the same table name in different schemas are reported as a
/// collision even though they are distinct physical tables. This is deliberate — <c>SchemaName</c>
/// may be <c>null</c> (auto-detected from the connection's search path at first use, after
/// validation), so the effective schema is not known when this check runs. Give each registration
/// a distinct table name, or its own data source / schema, to keep the check meaningful.
/// </para>
/// </remarks>
internal sealed class PostgresFileStorageTableRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _tableByRegistration = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _registrationByTable = new(StringComparer.Ordinal);

    /// <summary>
    /// Claims <paramref name="tableName"/> for the registration named <paramref name="registrationName"/>.
    /// </summary>
    /// <param name="registrationName">The registration's options-instance name (its key), or <c>null</c> when unnamed (nothing is tracked).</param>
    /// <param name="tableName">The table the registration declares.</param>
    /// <returns><c>null</c> when the claim succeeded; otherwise a message describing the collision.</returns>
    public string? Claim(string? registrationName, string tableName)
    {
        // An unnamed options instance cannot be told apart from another unnamed one, so tracking it
        // would make the first such registration block every other. The keyed API always names them.
        if (registrationName is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_tableByRegistration.TryGetValue(registrationName, out string? claimed))
            {
                // Re-validation of the same instance: its own table is not a collision, but a change
                // of table after the fact would silently free the old name and could collide.
                return string.Equals(claimed, tableName, StringComparison.Ordinal)
                    ? null
                    : $"The PostgreSQL file storage \"{registrationName}\" changed its TableName from " +
                      $"\"{claimed}\" to \"{tableName}\" after it was registered. TableName is fixed " +
                      "per registration.";
            }

            if (_registrationByTable.TryGetValue(tableName, out string? owner)
                && !string.Equals(owner, registrationName, StringComparison.Ordinal))
            {
                return $"TableName \"{tableName}\" is already used by the PostgreSQL file storage " +
                       $"registered with key \"{owner}\". Every keyed storage owns its own table — " +
                       "pick a distinct TableName for this registration.";
            }

            _tableByRegistration[registrationName] = tableName;
            _registrationByTable[tableName] = registrationName;
            return null;
        }
    }
}
