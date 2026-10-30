namespace Sa.Utils.WorkQueue;

public enum SaReaderCancelMode
{
    /// <summary>
    /// Hard cancel: an in-flight work item is cancelled immediately when its reader is removed.
    /// </summary>
    /// <remarks>
    /// Default mode. Every cancellation path (limit decrease, pause, force cancel, shutdown)
    /// interrupts the in-flight item, which is reported as <see cref="SaWorkStatus.Cancelled"/>.
    /// </remarks>
    Hard = 0,

    /// <summary>
    /// Soft cancel: a removed reader finishes its current work item before exiting.
    /// </summary>
    /// <remarks>
    /// Applies to limit decrease and pause (<see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/>):
    /// the in-flight item completes normally (<see cref="SaWorkStatus.Completed"/>) and items
    /// still in the channel remain queued for the remaining or replacement readers.
    /// Force cancel and shutdown always interrupt in-flight work regardless of this mode.
    /// </remarks>
    Soft = 1
}
