namespace Sa.Outbox.PostgreSql.Commands;


/// <summary>One row of the provider's error log.</summary>
public record struct ErrorInfo
{
    /// <summary>The error row id.</summary>
    public long ErrorId { get; init; }

    /// <summary>The recorded exception type name.</summary>
    public string TypeName { get; init; }

    /// <summary>When the error was recorded.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}


internal interface IErrorDeliveryCommand
{
    Task<IReadOnlyDictionary<Exception, ErrorInfo>> ExecuteAsync(
        ReadOnlyMemory<IOutboxContext> messages,
        CancellationToken cancellationToken);
}
