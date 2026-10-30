using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

/// <summary>
/// Defines a configuration pipeline for hybrid file storage interceptors and storage providers.
/// </summary>
public interface IHybridFileStorageConfiguration
{
    /// <summary>
    /// Configures interceptors that can observe or modify upload/download/delete operations.
    /// </summary>
    /// <param name="configure">An action that receives an <see cref="IInterceptorContainer"/> for registering interceptor implementations.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IHybridFileStorageConfiguration ConfigureInterceptors(
        Action<IServiceProvider, IInterceptorContainer> configure);

    /// <summary>
    /// Configures storage providers that will participate in the hybrid file storage system.
    /// </summary>
    /// <param name="configure">An action that receives a <see cref="HybridFileStorageContainerConfiguration"/> for registering storage implementations.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IHybridFileStorageConfiguration ConfigureStorage(
        Action<IServiceProvider, HybridFileStorageContainerConfiguration> configure);

    /// <summary>
    /// Enables automatic logging of file storage operations through registered interceptors.
    /// </summary>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IHybridFileStorageConfiguration AddLogging();

    /// <summary>
    /// Sets the global storage-type order used to build each basket's failover chain.
    /// </summary>
    /// <param name="storageTypes">Storage types in the order they should be tried (e.g. <c>"fs"</c>, <c>"pg"</c>, <c>"s3"</c>, <c>"mem"</c>).</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    /// <remarks>
    /// Without an order the chain follows the registration order (the order of the provider
    /// <c>Add...</c> calls) and every registered type participates. A non-empty order changes that:
    /// a listed type without a registered storage is skipped silently; a registered type that is
    /// not listed does not participate in the operations of the basket at all (it is neither
    /// written to nor probed on read); storages of one type keep their registration order.
    /// An empty list (or only whitespace entries) leaves the order unset, and an empty list is
    /// not an error. The last call wins; for one basket only, use
    /// <see cref="OrderBasketTypes"/> — a non-empty basket override wins over this global list.
    /// </remarks>
    IHybridFileStorageConfiguration OrderTypes(params string[] storageTypes);

    /// <summary>
    /// Sets the storage-type order for a single basket, overriding the global
    /// <see cref="OrderTypes"/> order for that basket.
    /// </summary>
    /// <param name="basket">The basket the override applies to.</param>
    /// <param name="storageTypes">Storage types in the order they should be tried for that basket.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    /// <remarks>
    /// A non-empty override wins over the global order; an empty override falls back to the global
    /// order. The rule for a listed-but-unregistered or unlisted-but-registered type is the same
    /// as in <see cref="OrderTypes"/>. The last call wins for a basket.
    /// </remarks>
    IHybridFileStorageConfiguration OrderBasketTypes(string basket, params string[] storageTypes);

    /// <summary>
    /// Reads the storage-type order from a configuration section, for the global list and
    /// the per-basket overrides.
    /// </summary>
    /// <param name="configSectionPath">The configuration section path, e.g. <c>"HybridFileStorage"</c>.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configSectionPath"/> is <c>null</c>.</exception>
    /// <remarks>
    /// <para>
    /// The section shape:
    /// <code>
    /// "HybridFileStorage": {
    ///   "TypeOrder": [ "fs", "pg" ],
    ///   "Baskets": {
    ///     "share": [ "pg", "fs" ]
    ///   }
    /// }
    /// </code>
    /// <c>TypeOrder</c> is the global list (the <see cref="OrderTypes"/> twin), <c>Baskets</c>
    /// maps a basket name to its override (the <see cref="OrderBasketTypes"/> twin). The section
    /// is read when the hybrid storage is first resolved, against the <c>IConfiguration</c> in the
    /// container — if none is registered, the section is skipped.
    /// </para>
    /// <para>
    /// Precedence is the stage-0 rule applied scope by scope, with code beating configuration:
    /// a non-empty <see cref="OrderTypes"/> list wins over <c>TypeOrder</c>, a non-empty
    /// <see cref="OrderBasketTypes"/> override wins over the basket's configured override;
    /// an empty code list is "no opinion" and the configured list applies. Within one scope the
    /// rules are unchanged: a non-empty basket override → a non-empty global list → the
    /// registration order; a listed type without a registered storage is skipped silently, an
    /// unlisted registered type does not participate. The last call wins for the path.
    /// </para>
    /// </remarks>
    IHybridFileStorageConfiguration FromConfiguration(string configSectionPath);
}
