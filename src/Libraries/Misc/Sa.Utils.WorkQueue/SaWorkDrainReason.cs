namespace Sa.Utils.WorkQueue;

/// <summary>
/// Why work items still sitting in the buffer are being dropped without being
/// processed. Internal: it only selects the exception text reported to the
/// status callback — the reported status is <see cref="SaWorkStatus.Faulted"/>
/// in both cases.
/// </summary>
internal enum SaWorkDrainReason
{
    /// <summary>The queue is being shut down for good.</summary>
    Shutdown = 0,

    /// <summary>Readers were emergency-stopped; the queue itself stays active.</summary>
    ForceCancel = 1
}
