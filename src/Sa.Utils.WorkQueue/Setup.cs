using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Utils.WorkQueue;

public static class Setup
{
    public static IServiceCollection AddSaWorkQueue<TInput>(
        this IServiceCollection services,
        Func<IServiceProvider, SaWorkQueueOptions<TInput>> configureOptions,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {

        services.Add(new ServiceDescriptor(
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

    public static IServiceCollection AddSaWorkQueue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor, TInput>(
        this IServiceCollection services,
        Func<IServiceProvider, SaWorkQueueOptions<TInput>, SaWorkQueueOptions<TInput>>? configureOptions = null)
        where TProcessor : class, ISaWork<TInput>
    {
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
    public static ISaWorkQueue<TInput> CreateSimple<TInput>(
        this ISaWork<TInput> processor,
        int? concurrency = null)
    {
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
    public static ISaWorkQueue<TInput> CreateSimple<TInput>(
        Func<TInput, CancellationToken, Task> process,
        int? concurrency = null)
    {
        var options = SaWorkQueueOptions<TInput>.Create(process)
            .WithConcurrencyLimit(concurrency ?? Environment.ProcessorCount);

        return new SaWorkQueue<TInput>(options);
    }
}
