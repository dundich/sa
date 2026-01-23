namespace Sa.Outbox.PostgreSql;

public static class PgOutboxTableSettingsExtensions
{
    /// <summary>
    /// Gets the fully qualified name of the Outbox table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the Outbox table.</returns>
    public static string GetQualifiedMsgTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.Message.TableName}""";

    /// <summary>
    /// Gets the fully qualified name of the delivery table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the delivery table.</returns>
    public static string GetQualifiedDeliveryTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.Delivery.TableName}""";

    /// <summary>
    /// Gets the fully qualified name of the type table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the type table.</returns>
    public static string GetQualifiedTypeTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.Type.TableName}""";

    /// <summary>
    /// Gets the fully qualified name of the offset table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the offset table.</returns>
    public static string GetQualifiedOffsetTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.Offset.TableName}""";

    /// <summary>
    /// Gets the fully qualified name of the error table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the error table.</returns>
    public static string GetQualifiedErrorTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.Error.TableName}""";

    /// <summary>
    /// Gets the fully qualified name of the task table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the task table.</returns>
    public static string GetQualifiedTaskTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.TaskQueue.TableName}""";

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
        string baseTableName,
        string schemaName)
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
