namespace Sa.Utils.WorkQueue;

public enum SaReaderScalingStrategy
{
    /// <summary>
    /// Cancel the most recent readers (LIFO).
    /// </summary>
    /// <remarks>Older readers live longer — useful for caches, connection pools.</remarks>
    Lifo = 0,

    /// <summary>
    /// Cancel the oldest readers (FIFO).
    /// </summary>
    /// <remarks>Even lifetime distribution — useful for resource rotation.</remarks>
    Fifo = 1,

    /// <summary>
    /// Round‑robin: cancel readers in cyclic order.
    /// </summary>
    RoundRobin = 2,

    /// <summary>
    /// Random: cancel random readers (uniform distribution).
    /// </summary>
    Random = 3
}
