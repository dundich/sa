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


    public static ISaWorkQueue<TInput> CreateSimple<TInput>(
        this ISaWork<TInput> processor,
        int concurrency = -1)
    {
        var options = SaWorkQueueOptions<TInput>.Create(processor)
            .WithConcurrencyLimit(concurrency > 0 ? concurrency : Environment.ProcessorCount);

        return new SaWorkQueue<TInput>(options);
    }

    public static ISaWorkQueue<TInput> CreateSimple<TInput>(
        Func<TInput, CancellationToken, Task> process,
        int concurrency = -1)
    {
        var options = SaWorkQueueOptions<TInput>.Create(process)
            .WithConcurrencyLimit(concurrency > 0 ? concurrency : Environment.ProcessorCount);

        return new SaWorkQueue<TInput>(options);
    }
}
