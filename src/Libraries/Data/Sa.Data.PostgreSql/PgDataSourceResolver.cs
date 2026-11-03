using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// Resolves the <see cref="IPgDataSource"/> a (optionally keyed) consumer should use, without
/// requiring the consumer to own one.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order:
/// <list type="number">
/// <item>the keyed <see cref="IPgDataSource"/> registered under <paramref name="key"/> (an explicit
/// <c>AddSaPostgreSqlDataSource(key, ...)</c>, or a chained <c>AddDataSource(...)</c>);</item>
/// <item>the default (unkeyed) <see cref="IPgDataSource"/> — the shared source every keyed module
/// falls back to when it does not pin one of its own;</item>
/// <item>an <see cref="NpgsqlDataSource"/> already registered in the container — the keyed one under
/// <paramref name="key"/> when present, the unkeyed one otherwise — wrapped into an
/// <see cref="IPgDataSource"/> that <b>reuses its connection pool</b> and never disposes it.</item>
/// </list>
/// </para>
/// <para>
/// The last step is what lets a consumer ride on an application-wide source registered by
/// <c>Npgsql.DependencyInjection</c> (<c>AddNpgsqlDataSource(...)</c>) or a hand-written
/// <c>services.AddSingleton&lt;NpgsqlDataSource&gt;(...)</c>, with no matching
/// <c>AddSaPostgreSqlDataSource</c> call. <see cref="IPgDataSource.Create(NpgsqlDataSource)"/> wraps
/// the source without taking ownership, so the wrapper is cheap and disposing it never closes the
/// borrowed pool.
/// </para>
/// <para>
/// A missing source altogether throws: at that point there is nothing to connect to, and failing with
/// a named exception on first resolve is better than an <c>ObjectDisposedException</c> or a null deep
/// inside a query.
/// </para>
/// </remarks>
public static class PgDataSourceResolver
{
    /// <summary>
    /// Resolves the data source for an optional key.
    /// </summary>
    /// <param name="serviceProvider">The service provider to resolve from.</param>
    /// <param name="key">
    /// The key of the consumer's own data source, or <c>null</c>/empty for the default one. The same
    /// key is used for the <see cref="NpgsqlDataSource"/> fallback.
    /// </param>
    /// <returns>The resolved <see cref="IPgDataSource"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="serviceProvider"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when neither an <see cref="IPgDataSource"/> nor an <see cref="NpgsqlDataSource"/> is
    /// registered.
    /// </exception>
    public static IPgDataSource Resolve(IServiceProvider serviceProvider, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var hasKey = !string.IsNullOrEmpty(key);

        if (hasKey && serviceProvider.GetKeyedService<IPgDataSource>(key) is { } keyed)
        {
            return keyed;
        }

        if (serviceProvider.GetService<IPgDataSource>() is { } shared)
        {
            return shared;
        }

        // No IPgDataSource at all: reuse the application's NpgsqlDataSource, if it registered one.
        // The same key is tried first (a per-database source), then the shared unkeyed one.
        NpgsqlDataSource? borrowed = hasKey
            ? serviceProvider.GetKeyedService<NpgsqlDataSource>(key)
              ?? serviceProvider.GetService<NpgsqlDataSource>()
            : serviceProvider.GetService<NpgsqlDataSource>();

        if (borrowed is not null)
        {
            return IPgDataSource.Create(borrowed);
        }

        throw new InvalidOperationException(
            "No IPgDataSource is registered" +
            (hasKey ? $" for key \"{key}\"" : "") +
            " and no NpgsqlDataSource is registered" +
            (hasKey ? $" for key \"{key}\" or without a key" : "") +
            " in the service collection. Register one with AddSaPostgreSqlDataSource(...), " +
            "AddNpgsqlDataSource(...), or services.AddSingleton<NpgsqlDataSource>(...).");
    }

    /// <summary>
    /// Tries to resolve the data source for an optional key, without throwing when the container
    /// simply has none.
    /// </summary>
    /// <remarks>
    /// Exists for callers that treat a missing source as "not configured yet" rather than as an
    /// error — a schema default, for instance, when the same registration is also used without a
    /// database (unit tests, a design-time tool). A <em>registered</em> source is still returned
    /// through its normal factory, so its own failures (such as options validation) surface exactly
    /// as they do from <see cref="Resolve"/>.
    /// </remarks>
    /// <param name="serviceProvider">The service provider to resolve from.</param>
    /// <param name="key">The key of the consumer's own data source, or <c>null</c>/empty for the default one.</param>
    /// <param name="dataSource">The resolved source, or <c>null</c> when the container has none.</param>
    /// <returns><see langword="true"/> when a source was resolved.</returns>
    public static bool TryResolve(IServiceProvider serviceProvider, string? key, out IPgDataSource? dataSource)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        try
        {
            dataSource = Resolve(serviceProvider, key);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Resolve throws exactly this when neither an IPgDataSource nor an NpgsqlDataSource is
            // registered. A failure inside a registered factory is a different type (options
            // validation, connection-string parsing), so it is not swallowed here.
            dataSource = null;
            return false;
        }
    }
}
