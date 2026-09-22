namespace Sa.Utils.WorkQueue;

public enum SaExecutionErrorStrategy
{
    /// <summary>Mark the item as Faulted and continue processing.</summary>
    Continue,

    /// <summary>Mark the item as Faulted and stop the current reader (it will be replaced).</summary>
    StopReader,

    /// <summary>Mark the item as Faulted and initiate a shutdown of the entire queue (default).</summary>
    ShutdownQueue
}
