using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Utils.WorkQueue;

/// <summary>
/// Default <see cref="IWorkQueueBuilder{TInput}"/>: accumulates the code half of a queue
/// registration — the processor factory, the callbacks, and the code defaults for
/// <see cref="SaWorkQueueSettings"/>.
/// </summary>
/// <remarks>
/// The settings defaults are applied to the options pipeline <b>before</b>
/// <c>BindConfiguration</c>, which is what makes configuration win: the binder only
/// overwrites the keys the section actually contains, everything else keeps these values.
/// </remarks>
internal sealed class WorkQueueBuilder<TInput> : IWorkQueueBuilder<TInput>
{
    private readonly IServiceCollection _services;

    private Func<IServiceProvider, ISaWork<TInput>>? _processorFactory;
    private Action<TInput, SaWorkStatus, Exception?>? _statusChanged;
    private Func<TInput, Exception, SaExecutionErrorStrategy>? _handleItemFaulted;
    private Func<TInput, string>? _getItemDisplayName;
    private TimeProvider? _timeProvider;
    private Func<IServiceProvider, SaWorkQueueOptions<TInput>, SaWorkQueueOptions<TInput>>? _escapeHatch;

    /// <summary>Code defaults for the settings half; copied into the options pipeline before binding.</summary>
    private readonly SaWorkQueueSettings _codeSettings = new();

    /// <summary>Pipeline actions collected via <see cref="Options"/>; replayed by Setup after code defaults and section binding.</summary>
    private readonly List<Action<OptionsBuilder<SaWorkQueueSettings>>> _settingsActions = [];

    public WorkQueueBuilder(IServiceCollection services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Whether any <c>With*</c> settings method was called — a no-op Configure is not registered.</summary>
    public bool HasCodeSettings { get; private set; }

    /// <summary>Processor factory captured from the <c>UseProcessor</c>/<c>WithProcess</c> calls; <see langword="null"/> when the container's own <see cref="ISaWork{TInput}"/> is the fallback.</summary>
    public Func<IServiceProvider, ISaWork<TInput>>? ProcessorFactory => _processorFactory;

    public Action<TInput, SaWorkStatus, Exception?>? StatusChanged => _statusChanged;

    public Func<TInput, Exception, SaExecutionErrorStrategy>? HandleItemFaulted => _handleItemFaulted;

    public Func<TInput, string>? GetItemDisplayName => _getItemDisplayName;

    public TimeProvider? TimeProvider => _timeProvider;

    public Func<IServiceProvider, SaWorkQueueOptions<TInput>, SaWorkQueueOptions<TInput>>? EscapeHatch => _escapeHatch;

    /// <summary>Pipeline actions collected via <see cref="Options"/>, in call order.</summary>
    public IReadOnlyList<Action<OptionsBuilder<SaWorkQueueSettings>>> SettingsActions => _settingsActions;

    /// <summary>Section recorded via <see cref="FromConfiguration"/>; bound by Setup in the fixed slot between the code defaults and <see cref="Options"/> actions.</summary>
    public string? ConfigSectionPath { get; private set; }

    // ---------- processor ----------

    public IWorkQueueBuilder<TInput> UseProcessor(ISaWork<TInput> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processorFactory = _ => processor;
        return this;
    }

    public IWorkQueueBuilder<TInput> UseProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>()
        where TProcessor : class, ISaWork<TInput>
    {
        // TryAdd: a container that already registers the processor keeps its registration
        // (including a decorated or mocked one).
        _services.TryAddSingleton<TProcessor>();
        _processorFactory = sp => sp.GetRequiredService<TProcessor>();
        return this;
    }

    public IWorkQueueBuilder<TInput> WithProcess(Func<TInput, CancellationToken, Task> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _processorFactory = _ => new DelegatingWork(process);
        return this;
    }

    // ---------- settings: code defaults ----------

    public IWorkQueueBuilder<TInput> WithQueueCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _codeSettings.QueueCapacity = capacity;
        HasCodeSettings = true;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        _codeSettings.ConcurrencyLimit = limit;
        HasCodeSettings = true;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithMaxConcurrency(int limit)
    {
        _codeSettings.MaxConcurrency = limit < 1 ? Environment.ProcessorCount : limit;
        HasCodeSettings = true;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithSingleWriter(bool singleWriter)
    {
        _codeSettings.SingleWriter = singleWriter;
        HasCodeSettings = true;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithEnqueueStrategy(SaEnqueueStrategy strategy)
    {
        _codeSettings.EnqueueStrategy = strategy;
        HasCodeSettings = true;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithReaderCancelMode(SaReaderCancelMode mode)
    {
        _codeSettings.ReaderCancelMode = mode;
        HasCodeSettings = true;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithReaderCancellationOrder(SaReaderCancellationOrder order)
    {
        _codeSettings.ReaderCancellationOrder = order;
        HasCodeSettings = true;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithShutdownTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _codeSettings.ShutdownTimeout = timeout;
        HasCodeSettings = true;
        return this;
    }

    // ---------- settings: the standard options pipeline ----------

    public IWorkQueueBuilder<TInput> FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);
        ConfigSectionPath = configSectionPath;
        return this;
    }

    public IWorkQueueBuilder<TInput> Options(Action<OptionsBuilder<SaWorkQueueSettings>> configureSettings)
    {
        ArgumentNullException.ThrowIfNull(configureSettings);
        _settingsActions.Add(configureSettings);
        return this;
    }

    // ---------- callbacks ----------

    public IWorkQueueBuilder<TInput> WithStatusCallback(Action<TInput, SaWorkStatus, Exception?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _statusChanged = callback;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithHandleItemFaulted(Func<TInput, Exception, SaExecutionErrorStrategy> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _handleItemFaulted = callback;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithItemDisplayName(Func<TInput, string> getDisplayName)
    {
        ArgumentNullException.ThrowIfNull(getDisplayName);
        _getItemDisplayName = getDisplayName;
        return this;
    }

    public IWorkQueueBuilder<TInput> WithTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        return this;
    }

    // ---------- escape hatch ----------

    public IWorkQueueBuilder<TInput> Configure(
        Func<IServiceProvider, SaWorkQueueOptions<TInput>, SaWorkQueueOptions<TInput>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _escapeHatch = configure;
        return this;
    }

    // ---------- pipeline helper ----------

    /// <summary>Called by the options pipeline before <c>BindConfiguration</c>: configuration overwrites what it mentions, the rest keeps these code defaults.</summary>
    public void ApplyCodeSettings(SaWorkQueueSettings target)
    {
        target.QueueCapacity = _codeSettings.QueueCapacity;
        target.ConcurrencyLimit = _codeSettings.ConcurrencyLimit;
        target.MaxConcurrency = _codeSettings.MaxConcurrency;
        target.SingleWriter = _codeSettings.SingleWriter;
        target.EnqueueStrategy = _codeSettings.EnqueueStrategy;
        target.ReaderCancelMode = _codeSettings.ReaderCancelMode;
        target.ReaderCancellationOrder = _codeSettings.ReaderCancellationOrder;
        target.ShutdownTimeout = _codeSettings.ShutdownTimeout;
    }

    /// <summary>Adapter from a processing delegate to <see cref="ISaWork{TInput}"/> — the same shape <see cref="SaWorkQueueOptions{TInput}.Create(Func{TInput, CancellationToken, Task})"/> builds.</summary>
    private sealed class DelegatingWork(Func<TInput, CancellationToken, Task> process) : ISaWork<TInput>
    {
        public Task Execute(TInput input, CancellationToken cancellationToken)
            => process(input, cancellationToken);
    }
}
