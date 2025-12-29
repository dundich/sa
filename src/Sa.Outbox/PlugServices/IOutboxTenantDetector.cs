namespace Sa.Outbox.PlugServices;

/// <summary>
/// Discovers tenant IDs at runtime from system data (e.g., message queues, inbox tables).
/// </summary>
/// </summary>
public interface IOutboxTenantDetector : ITenantSource
{
    /// <summary>
    /// Detects available tenant IDs from data sources.
    /// </summary>
    bool CanDetect { get; }
}
