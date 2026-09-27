using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Utils.WorkQueue;

public static class Setup
{
    /// <summary>Registers a work queue built from an options factory.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Factory that builds the options for <typeparamref name="TInput"/>.</param>
    /// <param name="lifetime">
    /// Registration lifetime. Defaults to <see cref="ServiceLifetime.Singleton"/>, which is the only
    /// sensible choice: the queue owns reader tasks and a bounded buffer, so creating several of
    /// them for one input type is rarely intended.
    /// </param>
    /// <remarks>
    /// Uses <c>TryAdd</c>: registering twice for the same <typeparamref name="TInput"/> keeps the first
    /// registration instead of making <c>GetRequiredService</c> throw on a duplicate service type.
    /// </remarks>
    public static IServiceCollection AddSaWorkQueue<TInput>(
        this IServiceCollection services,
        Func<IServiceProvider, SaWorkQueueOptions<TInput>> configureOptions,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.TryAdd(new ServiceDescriptor(
            typeof(ISaWorkQueue<TInput>),
            sp =>
            {
                var options = configureOptions(sp);
                var logger = sp.GetService<ILogger<SaWorkQueue<TInput>>>();
                return new SaWorkQueue<TInput>(options, logger);
            },
            lifetime));

        return services;
    }

    /// <summary>Registers a singleton work queue around a <typeparamref name="TProcessor"/> resolved from DI.</summary>
    /// <typeparam name="TProcessor">The <see cref="ISaWork{TInput}"/> implementation; registered if absent.</typeparam>
    /// <typeparam name="TInput">The work item type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional hook to adjust the generated options.</param>
    public static IServiceCollection AddSaWorkQueue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor, TInput>(
        this IServiceCollection services,
        Func<IServiceProvider, SaWorkQueueOptions<TInput>, SaWorkQueueOptions<TInput>>? configureOptions = null)
        where TProcessor : class, ISaWork<TInput>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TProcessor>();

        configureOptions ??= (_, opts) => opts;

        services.TryAddSingleton<ISaWorkQueue<TInput>>(
            sp =>
            {
                var processor = sp.GetRequiredService<TProcessor>();
                var options = configureOptions(sp, SaWorkQueueOptions<TInput>.Create(processor));
                var logger = sp.GetService<ILogger<SaWorkQueue<TInput>>>();
                return new SaWorkQueue<TInput>(options, logger);
            });

        return services;
    }


    /// <summary>Creates a work queue from a processor with a fixed concurrency.</summary>
    /// <param name="processor">The processor that executes each work item.</param>
    /// <param name="concurrency">
    /// The number of parallel readers. <c>null</c> (default) means automatic
    /// (processor count); <c>0</c> starts the queue paused (no readers);
    /// values above the automatic max are clamped to it.
    /// </param>
    /// <remarks>
    /// <see cref="MaxConcurrency"/> is left at the processor count, so the queue cannot scale above it
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
