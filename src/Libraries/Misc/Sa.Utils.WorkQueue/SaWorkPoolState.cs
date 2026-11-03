namespace Sa.Utils.WorkQueue;

/// <summary>
/// The condition of a queue's reader pool, as reported by
/// <see cref="ISaWorkQueue{TInput}.PoolState"/>.
/// </summary>
/// <remarks>
/// It exists because <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/> cannot answer
/// the question on its own. A force-cancel releases the slots of the readers it stops,
/// so the limit drops to <c>0</c> — the same number a deliberate pause produces. A count
/// of live readers does not help either: it is <c>0</c> in both cases as well. Two
/// entirely different situations, one pair of numbers, no way to tell them apart from
/// outside. This enum is that way out.
/// </remarks>
public enum SaWorkPoolState
{
    /// <summary>
    /// The queue is running: not paused, and at least one reader is live.
    /// </summary>
    /// <remarks>
    /// Items are being worked on and the pool can make progress. Note that the number of
    /// readers is not itself reported — a pool that is <em>Active</em> but smaller than
    /// its limit is still answering this question correctly.
    /// </remarks>
    Active = 0,

    /// <summary>
    /// The queue was paused on purpose: <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/>
    /// was set to <c>0</c> and nothing has raised it again.
    /// </summary>
    /// <remarks>
    /// This is a deliberate state, not a fault. Items that arrive are queued and stay
    /// queued. Raising the limit above <c>0</c> resumes processing.
    /// <para>
    /// A queue is only <em>Paused</em> while it is enabled. A stopped queue never reports
    /// this, even if the limit was <c>0</c> when it was stopped: the pause is no longer the
    /// reason nothing is happening, the shutdown is.
    /// </para>
    /// </remarks>
    Paused = 1,

    /// <summary>
    /// The queue is enabled and not paused, but has no readers left: every one of them was
    /// force-cancelled, lost to a fault, or cancelled by a limit decrease, and nothing
    /// re-armed the pool.
    /// </summary>
    /// <remarks>
    /// This is the one state that means something is wrong, and it is the reason this enum
    /// exists — it is indistinguishable from <see cref="Paused"/> by any number the queue
    /// used to publish. Items that arrive will be queued and nothing will take them.
    /// <para>
    /// Setting <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/> to a value above
    /// <c>0</c> re-arms the pool and returns the queue to <see cref="Active"/>.
    /// </para>
    /// </remarks>
    NoReaders = 2,

    /// <summary>
    /// The queue has been shut down or disposed. There is no pool to report on.
    /// </summary>
    /// <remarks>
    /// The terminal state, and the reason this enum covers the whole life of a queue: an
    /// answer that said only <see cref="Paused"/>, <see cref="NoReaders"/> or
    /// <see cref="Active"/> would be claiming a running pool for a queue that is not
    /// running, and a caller would be back to combining fields by hand.
    /// </remarks>
    Stopped = 3
}
