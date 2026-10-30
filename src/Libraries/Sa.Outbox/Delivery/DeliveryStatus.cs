namespace Sa.Outbox.Delivery;

/// <summary>
/// Represents the status of a delivery attempt.
/// </summary>
/// <param name="Code">The status code representing the result of the delivery.</param>
/// <param name="Message">A message providing additional context about the delivery status.</param>
/// <param name="CreatedAt">The date and time when the status was created.</param>
public readonly record struct DeliveryStatus(
    DeliveryStatusCode Code,
    string Message,
    DateTimeOffset CreatedAt
);
