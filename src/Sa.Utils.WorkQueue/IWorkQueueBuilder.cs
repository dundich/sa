using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Sa.Utils.WorkQueue;

/// <summary>
/// Configures the code half of a work queue registered through
/// <see cref="Setup.AddSaWorkQueue{TInput}"/>: the processor, the callbacks, and the
/// code defaults for the settings that can also come from configuration.
/// </summary>
/// <typeparam name="TInput">The work item type.</typeparam>
/// <remarks>
/// Everything here is what <c>appsettings</c> cannot carry. The serializable half
/// (<see cref="SaWorkQueueSettings"/> — capacity, concurrency, strategies, timeouts) has
/// its own <c>With*</c> methods on this builder as code defaults, plus the standard
/// options pipeline: configuration binds after them and therefore wins where it speaks.
/// <para>
/// Two escape hatches sit above configuration: <see cref="Options"/> for the settings
/// half (its <c>Configure</c> beats the bound section) and <see cref="Configure"/>, which
/// runs when the queue is resolved with the fully merged options and gets the last word
/// over everything — use it when a callback needs services from the container.
/// </para>
/// </remarks>
public interface IWorkQueueBuilder<TInput>
{
    // ---------- processor ----------

    /// <summary>Uses the given processor instance.</summary>
    IWorkQueueBuilder<TInput> UseProcessor(ISaWork<TInput> processor);

    /// <summary>
    /// Uses <typeparamref name="TProcessor"/>, registered in the container if absent.
    /// </summary>
    IWorkQueueBuilder<TInput> UseProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>()
        where TProcessor : class, ISaWork<TInput>;

    /// <summary>Uses a delegate as the processor.</summary>
    /// <param name="process">Async delegate that receives the item and a cancellation token.</param>
    IWorkQueueBuilder<TInput> WithProcess(Func<TInput, CancellationToken, Task> process);

    // ---------- settings: code defaults (configuration wins where it speaks) ----------

    /// <summary>Sets the default channel capacity. Must be at least 1.</summary>
    IWorkQueueBuilder<TInput> WithQueueCapacity(int capacity);

    /// <summary>
    /// Sets the default number of parallel readers: <c>0</c> pauses processing,
    /// otherwise must be positive.
    /// </summary>
    IWorkQueueBuilder<TInput> WithConcurrencyLimit(int limit);

    /// <summary>Sets the default absolute ceiling of reader tasks. Values below 1 default to the CPU count.</summary>
    IWorkQueueBuilder<TInput> WithMaxConcurrency(int limit);

    /// <summary>Optimises the channel for a single writer source.</summary>
    IWorkQueueBuilder<TInput> WithSingleWriter(bool singleWriter);

    /// <summary>Sets the default behaviour when the buffer is full.</summary>
    IWorkQueueBuilder<TInput> WithEnqueueStrategy(SaEnqueueStrategy strategy);

    /// <summary>Sets how in-flight work is treated when readers are cancelled.</summary>
    IWorkQueueBuilder<TInput> WithReaderCancelMode(SaReaderCancelMode mode);

    /// <summary>Sets which readers are cancelled when the concurrency limit decreases.</summary>
    IWorkQueueBuilder<TInput> WithReaderCancellationOrder(SaReaderCancellationOrder order);

    /// <summary>Sets the default max wait for readers during shutdown or force-cancel. Must be positive.</summary>
    IWorkQueueBuilder<TInput> WithShutdownTimeout(TimeSpan timeout);

    // ---------- settings: the standard options pipeline ----------

    /// <summary>
    /// Binds <see cref="SaWorkQueueSettings"/> from the given configuration section,
    /// e.g. <c>"WorkQueue"</c> — the section otherwise passed as <c>AddSaWorkQueue</c>'s
    /// <c>configSectionPath</c> argument, moved into the one registration delegate.
    /// </summary>
    /// <remarks>
    /// Recorded on the builder; <c>AddSaWorkQueue</c> binds it in the fixed slot between
    /// the <c>With*</c> code defaults and the <see cref="Options"/> actions — so
    /// configuration wins over code no matter where this call sits in the chain, and an
    /// <see cref="Options"/> <c>Configure</c> always wins over configuration. May be
    /// called several times; the last path wins.
    /// </remarks>
    /// <param name="configSectionPath">Configuration section path, e.g. <c>"WorkQueue"</c>.</param>
    IWorkQueueBuilder<TInput> FromConfiguration(string configSectionPath);

    /// <summary>
    /// Hands the standard <see cref="OptionsBuilder{TOptions}"/> for
    /// <see cref="SaWorkQueueSettings"/> to the callback — the <c>Configure</c> /
    /// <c>PostConfigure</c> / <c>Validate</c> surface of the options pipeline, in one
    /// registration channel next to the processor and the callbacks.
    /// </summary>
    /// <remarks>
    /// Runs in the options pipeline <b>after</b> the <c>With*</c> code defaults and after
    /// <c>BindConfiguration</c>, so a <c>Configure</c> here is the settings-level escape
    /// hatch that beats configuration, while a <c>Validate</c> adds to — rather than
    /// replaces — the built-in checks. May be called several times; the actions run in
    /// call order.
    /// <para>
    /// For the fully merged queue options (processor, callbacks, DI) see the builder's
    /// <c>Configure((sp, opts) =&gt; ...)</c> escape hatch; this method is for the
    /// serializable settings half only.
    /// </para>
    /// </remarks>
    /// <param name="configureSettings">Callback receiving the settings options builder.</param>
    IWorkQueueBuilder<TInput> Options(Action<OptionsBuilder<SaWorkQueueSettings>> configureSettings);

    // ---------- callbacks: not serializable ----------

    /// <summary>Registers a callback for status changes of work items.</summary>
    IWorkQueueBuilder<TInput> WithStatusCallback(Action<TInput, SaWorkStatus, Exception?> callback);

    /// <summary>
    /// Sets the error handling strategy when an item fails. When not set, every failure
    /// shuts the queue down (<see cref="SaExecutionErrorStrategy.ShutdownQueue"/>).
    /// </summary>
    IWorkQueueBuilder<TInput> WithHandleItemFaulted(Func<TInput, Exception, SaExecutionErrorStrategy> callback);

    /// <summary>Sets a function to obtain a display name for each work item (e.g., for logging).</summary>
    IWorkQueueBuilder<TInput> WithItemDisplayName(Func<TInput, string> getDisplayName);

    /// <summary>
    /// Sets the clock behind every bounded wait inside the queue. Tests only — production
    /// callers should leave this alone; see <see cref="SaWorkQueueOptions{TInput}.WithTimeProvider"/>.
    /// </summary>
    IWorkQueueBuilder<TInput> WithTimeProvider(TimeProvider timeProvider);

    // ---------- escape hatch ----------

    /// <summary>
    /// Runs when the queue is resolved, on the fully merged options, and may replace them.
    /// The last word over everything — configuration, <see cref="Options"/> and the
    /// <c>With*</c> defaults alike; use it when a callback needs services from the container.
    /// </summary>
    /// <example>
    /// <code>
    /// .Configure((sp, opts) =>
    /// {
    ///     var log = sp.GetRequiredService&lt;ILogger&lt;OrderWork&gt;&gt;();
    ///     return opts.WithStatusCallback((item, status, _) => log.LogDebug("{Id} {Status}", item, status));
    /// })
    /// </code>
    /// </example>
    /// <remarks>
    /// Capture only singletons from the container: the queue is a singleton, so a captured
    /// scoped service would live forever.
    /// </remarks>
    /// <param name="configure">Factory invoked with the container and the merged options.</param>
    IWorkQueueBuilder<TInput> Configure(Func<IServiceProvider, SaWorkQueueOptions<TInput>, SaWorkQueueOptions<TInput>> configure);
}
