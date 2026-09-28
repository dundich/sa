namespace Sa.Utils.WorkQueue;

/// <summary>
/// Why <see cref="ISaWorkQueue.WaitForIdleAsync"/> gave up waiting for the queue
/// to drain. Internal: it only selects the text of a warning, since the two causes
/// are indistinguishable — and equally unfixable without the caller — to whoever
/// is waiting.
/// </summary>
internal enum SaWorkNoProgress
{
    /// <summary>
    /// The queue is paused on purpose: <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/>
    /// was set to <c>0</c> and nothing raised it again.
    /// </summary>
    Paused = 0,

    /// <summary>
    /// The queue is active but its pool is empty — every reader was
    /// force-cancelled or lost to a fault, and nothing re-armed it.
    /// </summary>
    NoReaders = 1
}
