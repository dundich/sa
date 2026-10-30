using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

internal sealed class HybridStorageBuilder(IServiceCollection services) : IHybridFileStorageConfiguration
{
    private readonly List<Action<IServiceProvider, HybridFileStorageContainerConfiguration>> _configureStorages = [];
    private readonly List<Action<IServiceProvider, IInterceptorContainer>> _configureInterceptors = [];
    private readonly List<Action<IServiceCollection>> _configureServices = [];
    private string[]? _typeOrder;
    private readonly Dictionary<string, IEnumerable<string>> _basketTypeOrder = new(StringComparer.Ordinal);
    private string? _orderSectionPath;
    private bool _logged = false;

    public IHybridFileStorageConfiguration ConfigureStorage(
        Action<IServiceProvider, HybridFileStorageContainerConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configureStorages.Add(configure);
        return this;
    }

    public IHybridFileStorageConfiguration ConfigureInterceptors(
        Action<IServiceProvider, IInterceptorContainer> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configureInterceptors.Add(configure);
        return this;
    }

    /// <summary>
    /// Queues registrations that must be applied to the service collection eagerly, before the
    /// container is built — needed by a provider that must appear in
    /// <c>sp.GetServices&lt;IFileStorage&gt;()</c> rather than only in the container.
    /// </summary>
    public void ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configureServices.Add(configure);
    }

    public IHybridFileStorageConfiguration AddLogging()
    {
        _logged = true;
        return this;
    }

    /// <inheritdoc/>
    public IHybridFileStorageConfiguration OrderTypes(params string[] storageTypes)
    {
        _typeOrder = storageTypes ?? [];
        return this;
    }

    /// <inheritdoc/>
    public IHybridFileStorageConfiguration OrderBasketTypes(string basket, params string[] storageTypes)
    {
        ArgumentNullException.ThrowIfNull(basket);

        _basketTypeOrder[basket] = storageTypes ?? [];
        return this;
    }

    /// <inheritdoc/>
    public IHybridFileStorageConfiguration FromConfiguration(string configSectionPath)
    {
        ArgumentNullException.ThrowIfNull(configSectionPath);

        _orderSectionPath = configSectionPath;
        return this;
    }

    public void Build()
    {
        if (_logged)
        {
            services.TryAddSingleton<UploadLoggingInterceptor>();
            services.TryAddSingleton<DownloadLoggingInterceptor>();
            services.TryAddSingleton<DeleteLoggingInterceptor>();
        }

        // Applied before the IHybridFileStorage factory is added, so a provider registered here is
        // already part of the collection when the factory runs at resolve time.
        foreach (var configure in _configureServices)
        {
            configure(services);
        }

        // The code-side order configuration is complete by build time (the configure pipeline has
        // already run). Without a configuration section the ordering is fully determined now; with
        // one the section is read against the container's IConfiguration at first resolve and
        // merged — code beats configuration, scope by scope.
        string? orderSectionPath = _orderSectionPath;
        string[]? codeTypeOrder = _typeOrder;
        Dictionary<string, IEnumerable<string>> codeBasketTypeOrder = _basketTypeOrder;

        services.TryAddSingleton<IHybridFileStorage>(sp =>
        {
            InterceptorContainer interceptorContainer = new();
            interceptorContainer.AddLoggingInterceptors(
                sp.GetService<UploadLoggingInterceptor>(),
                sp.GetService<DownloadLoggingInterceptor>(),
                sp.GetService<DeleteLoggingInterceptor>());

            foreach (var configure in _configureInterceptors)
            {
                configure(sp, interceptorContainer);
            }

            StorageTypeOrdering typeOrdering =
                CreateTypeOrdering(sp, orderSectionPath, codeTypeOrder, codeBasketTypeOrder);
            HybridFileStorageContainer storageContainer = new(sp.GetServices<IFileStorage>(), typeOrdering);

            var storageConfig = new HybridFileStorageContainerConfiguration(storageContainer.AddStorage);
            foreach (var configure in _configureStorages)
            {
                configure(sp, storageConfig);
            }

            // A container with no storage resolves successfully and then fails every operation
            // with HybridFileStorageNoAvailableException, which points at the call site rather
            // than at the missing registration. Fail here instead, where the cause is visible.
            if (!storageContainer.Storages.Any())
            {
                throw new InvalidOperationException(
                    "No IFileStorage provider is available to the hybrid container. " +
                    "Register at least one provider — for example " +
                    "services.AddSaInMemoryFileStorage() or services.AddSaFileSystemFileStorage(...) — " +
                    "or add one explicitly via IHybridFileStorageConfiguration.ConfigureStorage.");
            }

            return new HybridFileStorage(storageContainer, interceptorContainer);
        });
    }

    /// <summary>
    /// Builds the effective type ordering: the code lists alone when no section was given or no
    /// <see cref="IConfiguration"/> is registered, otherwise the code lists merged with the
    /// section — a non-empty code list wins per scope, an empty one is "no opinion" and the
    /// configured list applies.
    /// </summary>
    private static StorageTypeOrdering CreateTypeOrdering(
        IServiceProvider sp,
        string? sectionPath,
        string[]? codeTypeOrder,
        Dictionary<string, IEnumerable<string>> codeBasketTypeOrder)
    {
        if (sectionPath is null || sp.GetService<IConfiguration>() is not { } configuration)
        {
            return new StorageTypeOrdering(codeTypeOrder, codeBasketTypeOrder);
        }

        IConfigurationSection section = configuration.GetSection(sectionPath);

        string[]? configTypeOrder = ReadOrder(section.GetSection(OrderSectionKeys.TypeOrder));
        var configBasketTypeOrder = ReadBasketOrders(section.GetSection(OrderSectionKeys.Baskets));

        string[]? globalOrder = NonEmpty(codeTypeOrder) ?? configTypeOrder;

        // Start from the code overrides — the ones left without a configured counterpart must
        // stay — and let a configured override in only where the code has no non-empty opinion.
        Dictionary<string, IEnumerable<string>> merged = new(codeBasketTypeOrder, StringComparer.Ordinal);
        foreach (var (basket, order) in configBasketTypeOrder)
        {
            if (!merged.TryGetValue(basket, out var codeOrder) || NonEmpty(codeOrder) is null)
            {
                merged[basket] = order;
            }
        }

        return new StorageTypeOrdering(globalOrder, merged);
    }

    /// <summary>The section's child keys: <c>TypeOrder</c> (global list) and <c>Baskets</c> (per-basket overrides).</summary>
    private static class OrderSectionKeys
    {
        public const string TypeOrder = "TypeOrder";
        public const string Baskets = "Baskets";
    }

    /// <summary>Reads an ordered list of storage types from a section's children; <c>null</c> when there is none.</summary>
    private static string[]? ReadOrder(IConfigurationSection section)
    {
        List<string> values = [];

        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                values.Add(child.Value!);
            }
        }

        return values.Count == 0 ? null : [.. values];
    }

    /// <summary>Reads the <c>Baskets</c> mapping (basket → ordered list), skipping empty lists.</summary>
    private static Dictionary<string, IEnumerable<string>> ReadBasketOrders(IConfigurationSection section)
    {
        Dictionary<string, IEnumerable<string>> orders = new(StringComparer.Ordinal);

        foreach (var basketSection in section.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(basketSection.Key))
            {
                continue;
            }

            if (ReadOrder(basketSection) is { } order)
            {
                orders[basketSection.Key] = order;
            }
        }

        return orders;
    }

    /// <summary>The list when it holds at least one non-whitespace entry, otherwise <c>null</c> ("not set").</summary>
    private static string[]? NonEmpty(IEnumerable<string>? storageTypes)
        => storageTypes is not null && storageTypes.Any(static t => !string.IsNullOrWhiteSpace(t))
            ? [.. storageTypes]
            : null;
}
