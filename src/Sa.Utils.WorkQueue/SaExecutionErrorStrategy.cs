namespace Sa.Utils.WorkQueue;

public enum SaExecutionErrorStrategy
{
    /// <summary>Mark the item as Faulted and continue processing.</summary>
    Continue,

    /// <summary>
    /// Mark the item as Faulted and stop the current reader. The reader is not replaced:
    /// the effective <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/> decreases by one
    /// (a "reader lost" warning is logged), and replacement readers appear only when the
    /// limit is set back to the target value.
    /// </summary>
    StopReader,

    /// <summary>Mark the item as Faulted and initiate a shutdown of the entire queue (default).</summary>
    ShutdownQueue
}
