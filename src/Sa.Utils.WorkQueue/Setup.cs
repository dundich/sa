using Microsoft.Extensions.Configuration.Binder.SourceGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sa.Utils.WorkQueue;

/// <summary>
/// Registration entry points for work queues: the standard options pipeline for the
/// serializable half (<see cref="SaWorkQueueSettings"/> from <c>appsettings</c>) and
/// <see cref="IWorkQueueBuilder{TInput}"/> for the code half (processor, callbacks).
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers a singleton work queue of <typeparamref name="TInput"/> on the standard
    /// options pipeline.
    /// </summary>
    /// <typeparam name="TInput">The work item type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// The code half: the processor, the callbacks, <c>With*</c> code defaults for
    /// <see cref="SaWorkQueueSettings"/>, the configuration section via
    /// <see cref="IWorkQueueBuilder{TInput}.FromConfiguration"/>, and — via
    /// <see cref="IWorkQueueBuilder{TInput}.Options"/> — the standard options pipeline
    /// (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) in the same delegate.
    /// Invoked once, immediately; its settings are put on the pipeline <b>before</b> the
    /// section binds, so configuration wins over them, while its <c>Options(...)</c>
    /// actions run after binding and therefore beat it.
    /// </param>
    /// <param name="lifetime">
    /// Registration lifetime. Only <see cref="ServiceLifetime.Singleton"/> is accepted:
    /// the queue owns reader tasks and a bounded buffer, so a scoped or transient
    /// registration would hand every resolution its own pool — and its own copy of every
    /// buffered item.
    /// </param>
    /// <returns>The <see cref="IServiceCollection"/> with the queue added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="lifetime"/> is not <see cref="ServiceLifetime.Singleton"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a second call carrying <paramref name="configure"/> is made in this
    /// collection for the same item type.
    /// </exception>
    /// <remarks>
    /// <b>Options pipeline order.</b> builder code defaults → <c>BindConfiguration</c>
    /// (section recorded via <c>FromConfiguration</c>) → the builder's <c>Options(...)</c>
    /// actions → validation. The configuration binder only
    /// overwrites keys the section contains, so configuration beats code and everything
    /// else the section does not mention keeps its code value; an <c>Options(...)</c>
    /// <c>Configure</c> is the escape hatch over both. <c>ValidateOnStart()</c> turns an
    /// invalid section into an <see cref="OptionsValidationException"/> at host start, and
    /// the same check fires when the settings are first read while the queue is created.
    /// <para>
    /// <b>Named options.</b> The settings instance is named <c>typeof(TInput).FullName</c>,
    /// so queues of different item types in one container bind from different sections.
    /// </para>
    /// <para>
    /// <b>Repeated calls.</b> Uses <c>TryAdd</c> for the queue itself: a second registration
    /// for the same <typeparamref name="TInput"/> keeps the first. A fully bare call
    /// (no arguments) neither checks nor creates the marker — the way repeated internal
    /// registrations deduplicate — while any call carrying configuration fails fast instead
    /// of silently stacking two <c>Configure</c> callbacks onto the same settings instance.
    /// </para>
    /// <para>
    /// The processor comes from <paramref name="configure"/> (<c>UseProcessor</c> /
    /// <c>WithProcess</c>) and falls back to an <see cref="ISaWork{TInput}"/> registered in
    /// the container, so a bare call works for a caller that registers its processor by hand.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaWorkQueue<TInput>(
        this IServiceCollection services,
        Action<IWorkQueueBuilder<TInput>>? configure = null,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Reject rather than accept: a scoped queue looks like it works right up
        // until two scopes both enqueue the same work. Checked first, so a rejection
        // leaves the collection untouched.
        if (lifetime != ServiceLifetime.Singleton)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), lifetime,
                "A work queue owns reader tasks and a bounded buffer, so it can only be registered as a singleton.");
        }

        // Both configuring intents (the section and the Options actions) live inside
        // `configure`, so `configure is not null` alone answers the guard — a bare call
        // cannot carry either.
        var willConfigure = configure is not null;

        if (willConfigure
            && services.Any(d => d.ServiceType == typeof(SaWorkQueueSettingsConfigurationMarker<TInput>)))
        {
            throw new InvalidOperationException(
                "AddSaWorkQueue options have already been configured for " + typeof(TInput).FullName + ". " +
                "A second configuring call would stack another Configure callback onto the same " +
                "SaWorkQueueSettings instance, so the settings would silently merge. " +
                "Configure the work queue options only once per item type.");
        }

        // The options pipeline, in the house order — with one deliberate inversion:
        // the builder's Configure goes on the pipeline *before* BindConfiguration, so
        // configuration wins over the code defaults (see AddSaWorkQueue remarks).
        var optionsBuilder = services.AddOptions<SaWorkQueueSettings>(QueueSettingsName<TInput>());

        WorkQueueBuilder<TInput>? builder = null;

        if (configure is not null)
        {
            builder = new WorkQueueBuilder<TInput>(services);
            configure(builder);

            if (builder.HasCodeSettings)
            {
                optionsBuilder.Configure(builder.ApplyCodeSettings);
            }
        }

        var sectionPath = builder?.ConfigSectionPath;

        if (sectionPath is not null)
        {
            optionsBuilder.BindConfiguration(sectionPath);
        }

        optionsBuilder.ValidateOnStart();

        // IValidateOptions rather than ValidateDataAnnotations(): the latter is marked
        // RequiresUnreferencedCode (IL2026) and breaks Native AOT. Registered by type via
        // TryAddEnumerable, so a repeated bare call deduplicates instead of stacking.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SaWorkQueueSettings>, SaWorkQueueSettingsValidator>());

        // Replayed in this slot — after the code defaults and the section binding — so an
        // Options(...).Configure has the last word over configuration, and its Validate
        // runs after this method's own checks.
        if (builder is not null)
        {
            foreach (var settingsAction in builder.SettingsActions)
            {
                settingsAction(optionsBuilder);
            }
        }

        // The first registration wins — the builder captured here is the one the queue is
        // built from, exactly as TryAdd kept the first factory before the builder existed.
        var capturedBuilder = builder;
        services.TryAdd(new ServiceDescriptor(
            typeof(ISaWorkQueue<TInput>),
            sp => CreateQueue(sp, capturedBuilder),
            lifetime));

        if (willConfigure)
        {
            services.TryAddSingleton(new SaWorkQueueSettingsConfigurationMarker<TInput>());
        }

        return services;
    }

    /// <summary>Options name for the queue of <typeparamref name="TInput"/>: one named settings instance per item type.</summary>
    private static string QueueSettingsName<TInput>()
        => typeof(TInput).FullName ?? typeof(TInput).Name;

    /// <summary>
    /// Assembles the queue at first resolve: settings from the options pipeline (whose
    /// read runs the validators), the builder's processor and callbacks, then the escape
    /// hatch — the only stage that runs after configuration.
    /// </summary>
    private static ISaWorkQueue<TInput> CreateQueue<TInput>(IServiceProvider services, WorkQueueBuilder<TInput>? builder)
    {
        var settings = services.GetRequiredService<IOptionsMonitor<SaWorkQueueSettings>>()
            .Get(QueueSettingsName<TInput>());

        var options = new SaWorkQueueOptions<TInput>(
            Processor: ResolveProcessor(services, builder),
            QueueCapacity: settings.QueueCapacity,
            ConcurrencyLimit: settings.ConcurrencyLimit,
            MaxConcurrency: settings.MaxConcurrency,
            SingleWriter: settings.SingleWriter,
            EnqueueStrategy: settings.EnqueueStrategy,
            ReaderCancelMode: settings.ReaderCancelMode,
            ReaderCancellationOrder: settings.ReaderCancellationOrder,
            HandleItemFaulted: builder?.HandleItemFaulted,
            StatusChanged: builder?.StatusChanged,
            GetItemDisplayName: builder?.GetItemDisplayName,
            ShutdownTimeout: settings.ShutdownTimeout,
            TimeProvider: builder?.TimeProvider);

        if (builder?.EscapeHatch is { } escapeHatch)
        {
            options = escapeHatch(services, options);
        }

        var logger = services.GetService<ILogger<SaWorkQueue<TInput>>>();

        return new SaWorkQueue<TInput>(options, logger);
    }

    private static ISaWork<TInput> ResolveProcessor<TInput>(IServiceProvider services, WorkQueueBuilder<TInput>? builder)
    {
        if (builder?.ProcessorFactory is { } processorFactory)
        {
            return processorFactory(services);
        }

        // No builder-configured processor: fall back to the container's own registration,
        // so a bare AddSaWorkQueue<TInput>() works for a caller that registers ISaWork<TInput>
        // by hand — and a plain "forgot the processor" reads as a normal DI miss.
        return services.GetService<ISaWork<TInput>>()
            ?? throw new InvalidOperationException(
                "No processor for the work queue of " + typeof(TInput).FullName + ". Call " +
                "AddSaWorkQueue(configure: b => b.UseProcessor<TProcessor>()) or register " +
                "ISaWork<" + typeof(TInput).Name + "> in the service collection.");
    }

    /// <summary>Creates a work queue from a processor with a fixed concurrency.</summary>
    /// <param name="processor">The processor that executes each work item.</param>
    /// <param name="concurrency">
    /// The number of parallel readers. <c>null</c> (default) means automatic
    /// (processor count); <c>0</c> starts the queue paused (no readers);
    /// values above the automatic max are clamped to it.
    /// </param>
    /// <remarks>
    /// <see cref="ISaWorkQueue{TInput}.MaxConcurrency"/> is left at the processor count, so the queue cannot scale above it
    /// at runtime. Use the options directly (and <c>WithMaxConcurrency</c>) when a higher ceiling
    /// is needed.
    /// </remarks>
    public static ISaWorkQueue<TInput> CreateSimple<TInput>(
        this ISaWork<TInput> processor,
        int? concurrency = null)
    {
        ArgumentNullException.ThrowIfNull(processor);

        var options = SaWorkQueueOptions<TInput>.Create(processor)
            .WithConcurrencyLimit(concurrency ?? Environment.ProcessorCount);

        return new SaWorkQueue<TInput>(options);
    }

    /// <summary>Creates a work queue from a processing delegate with a fixed concurrency.</summary>
    /// <param name="process">Async delegate that receives the item and a cancellation token.</param>
    /// <param name="concurrency">
    /// The number of parallel readers. <c>null</c> (default) means automatic
    /// (processor count); <c>0</c> starts the queue paused (no readers);
    /// values above the automatic max are clamped to it.
    /// </param>
    /// <remarks>
    /// <see cref="ISaWorkQueue{TInput}.MaxConcurrency"/> is left at the processor count, so the queue
    /// cannot scale above it at runtime. Use the options directly (and <c>WithMaxConcurrency</c>)
    /// when a higher ceiling is needed.
    /// </remarks>
    public static ISaWorkQueue<TInput> CreateSimple<TInput>(
        Func<TInput, CancellationToken, Task> process,
        int? concurrency = null)
    {
        ArgumentNullException.ThrowIfNull(process);

        var options = SaWorkQueueOptions<TInput>.Create(process)
            .WithConcurrencyLimit(concurrency ?? Environment.ProcessorCount);

        return new SaWorkQueue<TInput>(options);
    }
}

/// <summary>
/// Sentinel marker recording that <see cref="Setup.AddSaWorkQueue{TInput}"/> was configured
/// (a <c>configure</c> callback — section via <c>FromConfiguration</c>, settings via
/// <c>Options(...)</c>) for
/// <typeparamref name="TInput"/> in this collection, so a second *configuring* call fails
/// fast instead of silently stacking two <c>Configure</c> callbacks onto the same
/// <see cref="SaWorkQueueSettings"/> instance. Fully bare calls neither check nor create it.
/// </summary>
internal sealed class SaWorkQueueSettingsConfigurationMarker<TInput>
{
}
