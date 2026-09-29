namespace Sa.Outbox.PostgreSql.Configuration;

public static class PgOutboxTableSettingsExtensions
{
    /// <summary>
    /// Escapes a PostgreSQL identifier by wrapping it in double quotes and doubling any internal quotes.
    /// Throws <see cref="ArgumentException"/> if the identifier contains characters that cannot be safely escaped
    /// (newline, backslash, or unpaired quote).
    /// </summary>
    public static string EscapeIdentifier(this string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException("Identifier cannot be null or empty.", nameof(identifier));

        // PostgreSQL identifiers must not contain newline or backslash even inside quotes.
        if (identifier.IndexOfAny(['\n', '\r', '\\']) >= 0)
            throw new ArgumentException(
                $"Identifier '{identifier}' contains characters that cannot be safely quoted.",
                nameof(identifier));

        var sb = new System.Text.StringBuilder(identifier.Length + 4);
        sb.Append('"');
        foreach (char c in identifier)
        {
            sb.Append(c == '"' ? "''" : c.ToString());
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Gets the fully qualified name of the Outbox table, including the schema.
    /// Names are safely quoted to prevent SQL injection.
    /// </summary>
    /// <returns>The qualified name of the Outbox table.</returns>
    public static string GetQualifiedMsgTableName(this PgOutboxTableSettings settings)
        => $"{settings.DatabaseSchemaName.EscapeIdentifier()}.{settings.Message.TableName.EscapeIdentifier()}";

    /// <summary>
    /// Gets the fully qualified name of the delivery table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the delivery table.</returns>
    public static string GetQualifiedDeliveryTableName(this PgOutboxTableSettings settings)
        => $"{settings.DatabaseSchemaName.EscapeIdentifier()}.{settings.Delivery.TableName.EscapeIdentifier()}";

    /// <summary>
    /// Gets the fully qualified name of the type table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the type table.</returns>
    public static string GetQualifiedTypeTableName(this PgOutboxTableSettings settings)
        => $"{settings.DatabaseSchemaName.EscapeIdentifier()}.{settings.Type.TableName.EscapeIdentifier()}";

    /// <summary>
    /// Gets the fully qualified name of the offset table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the offset table.</returns>
    public static string GetQualifiedOffsetTableName(this PgOutboxTableSettings settings)
        => $"{settings.DatabaseSchemaName.EscapeIdentifier()}.{settings.Offset.TableName.EscapeIdentifier()}";

    /// <summary>
    /// Gets the fully qualified name of the error table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the error table.</returns>
    public static string GetQualifiedErrorTableName(this PgOutboxTableSettings settings)
        => $"{settings.DatabaseSchemaName.EscapeIdentifier()}.{settings.Error.TableName.EscapeIdentifier()}";

    /// <summary>
    /// Gets the fully qualified name of the task table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the task table.</returns>
    public static string GetQualifiedTaskTableName(this PgOutboxTableSettings settings)
        => $"{settings.DatabaseSchemaName.EscapeIdentifier()}.{settings.TaskQueue.TableName.EscapeIdentifier()}";

    /// <summary>
    /// Configures all table names based on a single base table name.
    /// </summary>
    /// <param name="settings">The settings instance.</param>
    /// <param name="baseTableName">Base name for all tables.</param>
    /// <returns>The configured settings instance.</returns>
    public static PgOutboxTableSettings UseBaseTableName(
        this PgOutboxTableSettings settings,
        string baseTableName)
    {
        if (string.IsNullOrWhiteSpace(baseTableName))
            throw new ArgumentException("Base table name cannot be null or empty", nameof(baseTableName));

        settings.TaskQueue.TableName = baseTableName;
        settings.Message.TableName = $"{baseTableName}{PgOutboxTableSettings.MessageTable.Suffix}";
        settings.Delivery.TableName = $"{baseTableName}{PgOutboxTableSettings.DeliveryTable.Suffix}";
        settings.Type.TableName = $"{baseTableName}{PgOutboxTableSettings.TypeTable.Suffix}";
        settings.Offset.TableName = $"{baseTableName}{PgOutboxTableSettings.OffsetTable.Suffix}";
        settings.Error.TableName = $"{baseTableName}{PgOutboxTableSettings.ErrorTable.Suffix}";

        return settings;
    }

    /// <summary>
    /// Configures all table names based on a single base table name with custom schema.
    /// </summary>
    public static PgOutboxTableSettings UseBaseTableName(
        this PgOutboxTableSettings settings,
        string schemaName,
        string baseTableName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentException("Schema name cannot be null or empty", nameof(schemaName));

        settings.UseBaseTableName(baseTableName);
        settings.DatabaseSchemaName = schemaName;

        return settings;
    }

    /// <summary>
    /// Sets custom schema name for all tables.
    /// </summary>
    public static PgOutboxTableSettings WithSchema(
        this PgOutboxTableSettings settings,
        string schemaName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
        settings.DatabaseSchemaName = schemaName;
        return settings;
    }

    /// <summary>
    /// Sets custom message table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithMsgTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.Message.TableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom delivery table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithDeliveryTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.Delivery.TableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom type table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithTypeTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.Type.TableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom offset table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithOffsetTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.Offset.TableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom error table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithErrorTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.Error.TableName = tableName;
        return settings;
    }
}
