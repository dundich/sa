using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;
using Sa.Partitional.PostgreSql;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests;

/// <summary>
/// When the Outbox data source has no connection string, it must reuse an
/// <see cref="NpgsqlDataSource"/> already registered in the container (sharing its pool) instead of
/// demanding its own connection settings.
/// </summary>
/// <remarks>
/// This is the connection-ownership path. The Outbox's <c>Sa.Partitional.PostgreSql</c> module is
/// keyed (<c>OutboxScope.DefaultKey</c>) while its <c>IPgDataSource</c> stays on the default key, so
/// the module's <c>PgDataSourceResolver</c> must fall back to the default data source, and the default
/// data source — with an empty connection string — must in turn borrow the registered
/// <see cref="NpgsqlDataSource"/>. The test drives migration and delivery end-to-end through that
/// borrowed pool, which only succeeds if the connection is real.
/// </remarks>
public sealed class OutboxBorrowedDataSourceTests(OutboxBorrowedDataSourceTests.Fixture fixture)
    : IClassFixture<OutboxBorrowedDataSourceTests.Fixture>
{
    sealed class BorrowedMessage
    {
        public string PayloadId { get; set; } = default!;
        public int TenantId { get; set; }
    }

    sealed class BorrowedConsumer : IConsumer<BorrowedMessage>
    {
        static int s_Counter;

        public static int Counter => Volatile.Read(ref s_Counter);

        public async ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<BorrowedMessage>> messages,
            CancellationToken cancellationToken)
        {
            Interlocked.Add(ref s_Counter, messages.Length);
            await Task.Delay(50, cancellationToken);
        }
    }

    public sealed class Fixture : PgDataSourceFixture
    {
        public Fixture()
        {
            // The application already owns a data source. Constructed lazily inside the factory:
            // the fixture constructor runs before the container starts, and ConnectionString needs
            // the mapped port. Registered through a factory so the container disposes it with the
            // provider (NpgsqlDataSource is not disposed twice: PgDataSource wraps a borrowed
            // instance with ownsDataSource: false).
            Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(ConnectionString));

            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, s) => s.WithTenantIds(1))
                    .WithDeliveries(b => b.AddDeliveryScoped<BorrowedConsumer, BorrowedMessage>(
                        "borrowed", (_, o) =>
                        {
                            // StartImmediately: the old delivery pre-seed did this implicitly; the
                            // default InitialDelay is 10s, beyond this test's wait budget.
                            o.StartImmediately()
                             .WithInterval(TimeSpan.FromMilliseconds(100))
                             .WithMaxBatchSize(1)
                             .WithNoBatchingWindow();
                        })))
                .AddSaOutboxUsingPostgreSql(cfg =>
                {
                    // Deliberately NO connection string — only the borrowed NpgsqlDataSource is
                    // available, so every query must go through its pool.
                    cfg.WithOutboxSettings((_, settings) =>
                    {
                        settings.TableSettings.DatabaseSchemaName = "borrowed";
                    });
                });
        }
    }

    IServiceProvider ServiceProvider => fixture.ServiceProvider;

    [Fact]
    public async Task Outbox_WithNoConnectionString_BorrowsRegisteredNpgsqlDataSource()
    {
        IScheduler scheduler = ServiceProvider
            .GetRequiredKeyedService<IScheduler>(OutboxScope.DefaultKey);
        Assert.True(await scheduler.Start(CancellationToken.None) >= 1);

        IOutboxMessagePublisher publisher =
            ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        ulong total = await publisher.Publish(
        [
            new BorrowedMessage { TenantId = 1 },
            new BorrowedMessage { TenantId = 1 },
        ], 1, TestContext.Current.CancellationToken);
        Assert.True(total > 0);

        // Migration executes DDL through the borrowed pool.
        IMigrationService migration = ServiceProvider
            .GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);
        Assert.True(
            await migration.WaitMigration(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "migration did not run through the borrowed NpgsqlDataSource");

        // Delivery reads the queue and updates statuses through the borrowed pool.
        int wait = 0;
        while (BorrowedConsumer.Counter < (int)total && wait++ < 20)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        await scheduler.Stop();

        Assert.Equal((int)total, BorrowedConsumer.Counter);
    }
}
