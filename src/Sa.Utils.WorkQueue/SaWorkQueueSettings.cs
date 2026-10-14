namespace Sa.Utils.WorkQueue;

/// <summary>
/// Serializable settings for a work queue, served by the standard
/// <c>Microsoft.Extensions.Options</c> pipeline: bound from a configuration section
/// (<see cref="IWorkQueueBuilder{TInput}.FromConfiguration"/> on
/// <see cref="Setup.AddSaWorkQueue{TInput}"/>) → validated
/// → read once when the queue is created.
/// </summary>
/// <remarks>
/// Only what fits in <c>appsettings</c> lives here. The runtime part — the processor and
/// the callbacks — stays on <see cref="SaWorkQueueOptions{TInput}"/> and is configured
/// through <see cref="IWorkQueueBuilder{TInput}"/>.
/// <para>
/// Each work item type owns its own named options instance (the name is
/// <c>typeof(TInput).FullName</c>), so two queues of different item types in one
/// container bind from different sections and never see each other's values.
/// </para>
/// <para>
/// <b>Precedence.</b> The options pipeline runs in a fixed order: the builder's
/// <c>With*</c> code defaults → <c>BindConfiguration</c> → the builder's
/// <c>Options(...)</c> actions. The configuration binder only overwrites keys that are
/// present in the section, so configuration wins over code and anything the section does
/// not mention keeps its code value; a <c>Configure</c> inside <c>Options(...)</c> is the
/// escape hatch that beats both.
/// </para>
/// <para>
/// A <see langword="null"/> value means "queue default" and is resolved by the queue
/// constructor: capacity falls back to the max concurrency, the concurrency limit to the
/// processor count, the shutdown timeout to 30 seconds.
/// </para>
/// </remarks>
public sealed class SaWorkQueueSettings
{
    /// <summary>Capacity of the bounded channel. <see langword="null"/> = equals max concurrency; must be at least 1.</summary>
    public int? QueueCapacity { get; set; }

    /// <summary>
    /// Number of parallel readers. <c>0</c> pauses processing (no readers);
    /// <see langword="null"/> = automatic (processor count); values above
    /// <see cref="MaxConcurrency"/> are clamped to it. Must not be negative.
    /// </summary>
    public int? ConcurrencyLimit { get; set; }

    /// <summary>
    /// Absolute ceiling of reader tasks. <c>0</c> or <see langword="null"/> = processor count.
    /// Must not be negative.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>Optimises the channel for a single writer source. <see langword="null"/> = <see langword="false"/>.</summary>
    public bool? SingleWriter { get; set; }

    /// <summary>What <c>Enqueue</c> does when the buffer is full. Default: <see cref="SaEnqueueStrategy.Wait"/>.</summary>
    public SaEnqueueStrategy EnqueueStrategy { get; set; } = SaEnqueueStrategy.Wait;

    /// <summary>
    /// How in-flight work is treated when readers are cancelled.
    /// Default: <see cref="SaReaderCancelMode.Hard"/>.
    /// </summary>
    public SaReaderCancelMode ReaderCancelMode { get; set; } = SaReaderCancelMode.Hard;

    /// <summary>
    /// Which readers are cancelled when the concurrency limit decreases.
    /// Default: <see cref="SaReaderCancellationOrder.Lifo"/>.
    /// </summary>
    public SaReaderCancellationOrder ReaderCancellationOrder { get; set; } = SaReaderCancellationOrder.Lifo;

    /// <summary>Max wait for readers during shutdown or force-cancel. <see langword="null"/> = 30 seconds; must be positive.</summary>
    public TimeSpan? ShutdownTimeout { get; set; }
}
