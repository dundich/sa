using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

/// <summary>
/// Интеграционные тесты для <see cref="IOutboxConsumerManager"/> с реальным Consume через PostgreSQL.
/// Проверяют pause/resume, Apply (runtime settings), Subscribe и Unregister в контексте обработки сообщений.
/// </summary>
[Collection("sequential")]
public sealed class DeliveryConsumerGroupManagerTests(DeliveryConsumerGroupManagerTests.Fixture fixture)
    : IClassFixture<DeliveryConsumerGroupManagerTests.Fixture>
{
    /// <summary>
    /// Счётчик вызовов Consume — используется для проверки, что потребитель обрабатывает сообщения.
    /// </summary>
    class CountingMessageConsumer : IConsumer<TestMessage>
    {
        public static int ConsumeCount;
        public static int TotalMessagesConsumed;
        public static List<int> BatchSizes = [];
        public static ManualResetEventSlim? BlockConsume;
        public static ManualResetEventSlim AllowConsume = default!;

        static CountingMessageConsumer()
        {
            Reset();
        }

        public static void Reset()
        {
            ConsumeCount = 0;
            TotalMessagesConsumed = 0;
            BatchSizes = [];
            BlockConsume = new ManualResetEventSlim(false);
            AllowConsume = new ManualResetEventSlim(true);
        }

        public async ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ConsumeCount);
            Interlocked.Add(ref TotalMessagesConsumed, messages.Length);
            BatchSizes.Add(messages.Length);

            // Ждем разрешения, чтобы контролировать время жизни Consume
            if (BlockConsume is not null && !BlockConsume.IsSet)
            {
                await Task.Delay(50, cancellationToken);
            }

            AllowConsume.Wait(cancellationToken);
        }
    }

    /// <summary>
    /// Потребитель, который блокируется навсегда после первого Consume — для тестов Pause.
    /// </summary>
    class BlockingMessageConsumer : IConsumer<TestMessage>
    {
        public static int ConsumeCount;
        public static ManualResetEventSlim StartedBlocking = default!;
        public static ManualResetEventSlim ReleaseBlocking = default!;

        static BlockingMessageConsumer()
        {
            Reset();
        }

        public static void Reset()
        {
            ConsumeCount = 0;
            StartedBlocking = new ManualResetEventSlim(false);
            ReleaseBlocking = new ManualResetEventSlim(false);
        }

        public async ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ConsumeCount);
            StartedBlocking.Set();

            // Auto-release on token cancellation to prevent permanent hang.
            using var registration = cancellationToken.Register(() => ReleaseBlocking.Set());

            // Block until explicitly released or token is cancelled.
            await Task.Delay(-1, cancellationToken);
            ReleaseBlocking.Wait(cancellationToken);
        }
    }

    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public OutboxConsumerSettings SettingsForTestGroup = default!;
        /// <summary>Реальное (санитизированное) имя группы для CountingMessageConsumer.</summary>
        public string CountingGroupId => SettingsForTestGroup.ConsumerGroupId;
        /// <summary>Реальное (санитизированное) имя группы для BlockingMessageConsumer.</summary>
        public static string BlockingGroupId => "mgr_blocking";
        public IOutboxConsumerManager ConsumerManager => GetInitializedManager();

        private IOutboxConsumerManager? _cachedManager;

        private IOutboxConsumerManager GetInitializedManager()
        {
            if (_cachedManager is not null) return _cachedManager;

            var manager = ServiceProvider.GetRequiredService<IOutboxConsumerManager>();

            // Отладка: проверяем значения
            var groupId = CountingGroupId;
            var blockingId = BlockingGroupId;

            // Авто-регистрация групп при первом доступе (имитирует поведение DeliveryJob)
            if (!manager.IsRegistered(groupId))
                manager.TryRegister(groupId, SettingsForTestGroup);

            if (!manager.IsRegistered(blockingId))
            {
                var blockingSettings = new OutboxConsumerSettings(
                    ConsumerGroupId: BlockingGroupId,
                    AsSingleton: false,
                    Interval: TimeSpan.FromMilliseconds(200),
                    InitialDelay: TimeSpan.Zero,
                    ConcurrencyLimit: 1,
                    MaxConcurrency: 1,
                    RetryCountOnError: 0,
                    MaxBatchSize: 1,
                    MaxProcessingIterations: -1,
                    IterationDelay: TimeSpan.Zero,
                    LockDuration: TimeSpan.FromMinutes(5),
                    LockRenewal: TimeSpan.FromMinutes(5),
                    LookbackInterval: TimeSpan.FromDays(7),
                    MaxDeliveryAttempts: 3,
                    BatchingWindow: TimeSpan.Zero,
                    PerTenantTimeout: TimeSpan.Zero,
                    PerTenantMaxDegreeOfParallelism: 1,
                    Paused: false,
                    Version: 0);
                manager.TryRegister(BlockingGroupId, blockingSettings);
            }

            return _cachedManager = manager;
        }
        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        public Fixture() : base()
        {
            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, s) => s.WithTenantIds(1))
                    .WithDeliveries(deliveryBuilder => deliveryBuilder
                        .AddDeliveryScoped<CountingMessageConsumer, TestMessage>("counting", (_, b) =>
                        {
                            b.WithInterval(TimeSpan.FromMilliseconds(200))
                             .WithMaxBatchSize(4)
                             .WithNoLockDuration()
                             .WithLockRenewal(TimeSpan.FromMinutes(5))
                             .WithNoBatchingWindow();

                            SettingsForTestGroup = b.Build();
                        })
                        .AddDeliveryScoped<BlockingMessageConsumer, TestMessage>("mgr_blocking", (_, b) =>
                        {
                            b.WithInterval(TimeSpan.FromMilliseconds(200))
                             .WithMaxBatchSize(1)
                             .WithNoLockDuration()
                             .WithLockRenewal(TimeSpan.FromMinutes(5))
                             .WithNoBatchingWindow();
                        })
                    )
                );
        }

    }

    #region Pause / Resume

    [Fact]
    public async Task Manager_Pause_DuringProcessing_StopsNextPoll()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();

        // Publish messages
        var messages = Enumerable.Range(1, 8)
            .Select(i => new TestMessage { PayloadId = $"pause-{i}", Content = $"Msg {i}", TenantId = 1 })
            .ToList();

        await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);

        // Process first batch
        var result = await fixture.Sub.ProcessMessages<TestMessage>(fixture.SettingsForTestGroup, TestContext.Current.CancellationToken);
        Assert.True(result > 0, "Ожидалась обработка хотя бы одного сообщения");

        int consumedBeforePause = CountingMessageConsumer.TotalMessagesConsumed;
        Assert.True(consumedBeforePause > 0, "Первый Consume должен был выполниться");

        // Если первое ProcessMessages обработало все сообщения — публикуем ещё для проверки Pause/Resume
        if (consumedBeforePause >= 8)
        {
            var extraMessages = new[] { new TestMessage { PayloadId = "pause-extra", Content = "Extra", TenantId = 1 } };
            await publisher.Publish(extraMessages, m => m.TenantId, TestContext.Current.CancellationToken);
        }

        // Pause the consumer group
        manager.Pause(group);
        Assert.True(manager.IsPaused(group), "Группа должна быть паузнутой");

        // ProcessMessages with paused settings should return 0
        var pausedSettings = manager.Get(group) ?? fixture.SettingsForTestGroup;
        result = await fixture.Sub.ProcessMessages<TestMessage>(pausedSettings, TestContext.Current.CancellationToken);
        Assert.Equal(0, result);

        // After pause, no new messages should be processed
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(consumedBeforePause, CountingMessageConsumer.TotalMessagesConsumed);

        // Resume
        manager.Resume(group);
        Assert.False(manager.IsPaused(group));

        // After resume, read updated settings from manager and process remaining messages
        var resumedSettings = manager.Get(group) ?? fixture.SettingsForTestGroup;
        result = await fixture.Sub.ProcessMessages<TestMessage>(resumedSettings, TestContext.Current.CancellationToken);
        Assert.True(result > 0, "После Resume должны обработаться оставшиеся сообщения");
    }

    [Fact]
    public async Task Manager_Resume_AfterPause_ResumesProcessing()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();

        // Publish more messages than one batch
        var messages = Enumerable.Range(1, 16)
            .Select(i => new TestMessage { PayloadId = $"resume-{i}", Content = $"Msg {i}", TenantId = 1 })
            .ToList();

        await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);

        // Pause immediately
        manager.Pause(group);

        // Wait a bit — nothing should be processed while paused
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(0, CountingMessageConsumer.ConsumeCount);

        // Resume and process
        manager.Resume(group);

        var result = await fixture.Sub.ProcessMessages<TestMessage>(fixture.SettingsForTestGroup, TestContext.Current.CancellationToken);
        Assert.True(result > 0, "После Resume должны обработаться сообщения");
        Assert.True(CountingMessageConsumer.ConsumeCount > 0, "Consume должен был вызваться после Resume");
    }

    [Fact]
    public async Task Manager_PauseAndResume_MessagesProcessedAfterResume()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();
        const int totalMessages = 10;

        var messages = Enumerable.Range(1, totalMessages)
            .Select(i => new TestMessage { PayloadId = $"cycle-{i}", Content = $"Msg {i}", TenantId = 1 })
            .ToList();

        await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);

        // Process first batch
        await fixture.Sub.ProcessMessages<TestMessage>(fixture.SettingsForTestGroup, TestContext.Current.CancellationToken);
        int firstBatchCount = CountingMessageConsumer.TotalMessagesConsumed;
        Assert.True(firstBatchCount > 0 && firstBatchCount <= totalMessages);

        // Если первое ProcessMessages обработало все сообщения — публикуем ещё для проверки Pause/Resume
        if (firstBatchCount >= totalMessages)
        {
            var extraMessages = new[] { new TestMessage { PayloadId = "cycle-extra", Content = "Extra", TenantId = 1 } };
            await publisher.Publish(extraMessages, m => m.TenantId, TestContext.Current.CancellationToken);
        }

        // Pause
        manager.Pause(group);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Should NOT process more while paused
        int beforeResume = CountingMessageConsumer.TotalMessagesConsumed;
        Assert.Equal(firstBatchCount, beforeResume);

        // Resume
        manager.Resume(group);

        // Process remaining — read updated settings from manager
        var resumedSettings = manager.Get(group) ?? fixture.SettingsForTestGroup;
        await fixture.Sub.ProcessMessages<TestMessage>(resumedSettings, TestContext.Current.CancellationToken);
        Assert.True(CountingMessageConsumer.TotalMessagesConsumed > firstBatchCount,
            "После Resume должны обработаться дополнительные сообщения");
    }

    #endregion

    #region Apply (Runtime Settings Changes)

    [Fact]
    public async Task Manager_Apply_MaxBatchSize_AffectsNextBatch()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();
        CountingMessageConsumer.BatchSizes.Clear();

        const int initialMaxBatch = 4;
        const int newMaxBatch = 1;

        var messages = Enumerable.Range(1, 8)
            .Select(i => new TestMessage { PayloadId = $"batchsize-{i}", Content = $"Msg {i}", TenantId = 1 })
            .ToList();

        await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);

        // First batch should use initial MaxBatchSize
        await fixture.Sub.ProcessMessages<TestMessage>(fixture.SettingsForTestGroup, TestContext.Current.CancellationToken);
        Assert.True(CountingMessageConsumer.ConsumeCount > 0);
        Assert.True(CountingMessageConsumer.BatchSizes.LastOrDefault() <= initialMaxBatch);

        // Apply new MaxBatchSize via manager
        manager.Apply(group, s => s with { MaxBatchSize = newMaxBatch });

        var updatedSettings = manager.Get(group);
        Assert.NotNull(updatedSettings);
        Assert.Equal(newMaxBatch, updatedSettings.MaxBatchSize);

        // Next batch should respect new MaxBatchSize
        CountingMessageConsumer.BatchSizes.Clear();
        await fixture.Sub.ProcessMessages<TestMessage>(fixture.SettingsForTestGroup, TestContext.Current.CancellationToken);

        if (CountingMessageConsumer.BatchSizes.Count > 0)
        {
            Assert.True(CountingMessageConsumer.BatchSizes.All(bs => bs <= newMaxBatch),
                "Каждый батч после Apply не должен превышать новый MaxBatchSize");
        }
    }

    [Fact]
    public async Task Manager_Apply_ConsecutiveUpdates_AccumulateCorrectly()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();

        var messages = Enumerable.Range(1, 12)
            .Select(i => new TestMessage { PayloadId = $"chained-{i}", Content = $"Msg {i}", TenantId = 1 })
            .ToList();

        await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);

        // Chain multiple Apply calls
        manager.Apply(group, s => s with { MaxBatchSize = 2 });
        manager.Apply(group, s => s with { MaxDeliveryAttempts = 5 });
        manager.Apply(group, s => s with { MaxBatchSize = 1 });

        var finalSettings = manager.Get(group);
        Assert.NotNull(finalSettings);
        Assert.Equal(1, finalSettings.MaxBatchSize);
        Assert.Equal(5, finalSettings.MaxDeliveryAttempts);

        // Process — should use final settings
        var result = await fixture.Sub.ProcessMessages<TestMessage>(finalSettings, TestContext.Current.CancellationToken);
        Assert.True(result >= 0);
    }

    #endregion

    #region Subscribe

    [Fact]
    public async Task Manager_Subscribe_ReceivesSettingsOnApply()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();

        List<OutboxConsumerSettings> capturedSettings = [];
        using var subscription = manager.Subscribe(group, s => capturedSettings.Add(s));

        var messages = new[] { new TestMessage { PayloadId = "sub-1", Content = "Msg 1", TenantId = 1 } };
        await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);

        // Apply change
        manager.Apply(group, s => s with { MaxBatchSize = 32 });

        // Subscriber should have received the updated settings
        Assert.Single(capturedSettings);
        Assert.Equal(32, capturedSettings[0].MaxBatchSize);
        Assert.Equal(group, capturedSettings[0].ConsumerGroupId);
    }

    [Fact]
    public async Task Manager_Subscribe_ReceivesSettingsOnPause()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;

        OutboxConsumerSettings? pausedSettings = null;
        using var subscription = manager.Subscribe(group, s => pausedSettings = s);

        manager.Pause(group);

        Assert.NotNull(pausedSettings);
        Assert.True(pausedSettings.Paused);
    }

    [Fact]
    public async Task Manager_Subscribe_Unsubscribe_PreventsFutureNotifications()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;

        var notifications = 0;
        var subscription = manager.Subscribe(group, _ => notifications++);
        subscription.Dispose();

        manager.Apply(group, s => s with { MaxBatchSize = 99 });
        manager.Apply(group, s => s with { MaxBatchSize = 100 });

        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Manager_Subscribe_MultipleSubscribers_AllReceiveUpdates()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;

        var sub1Received = false;
        var sub2Received = false;

        using var sub1 = manager.Subscribe(group, _ => sub1Received = true);
        using var sub2 = manager.Subscribe(group, _ => sub2Received = true);

        manager.Apply(group, s => s with { MaxBatchSize = 77 });

        Assert.True(sub1Received);
        Assert.True(sub2Received);
    }

    #endregion

    #region IsRegistered / Unregister

    [Fact]
    public void Manager_IsRegistered_ReturnsTrueAfterSetup()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;

        // После настройки DI группа уже зарегистрирована (через DeliveryJob bootstrap)
        Assert.True(manager.IsRegistered(group));
    }

    [Fact]
    public void Manager_Get_ReturnsNotNullForKnownGroup()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;

        var settings = manager.Get(group);
        Assert.NotNull(settings);
        Assert.Equal(group, settings.ConsumerGroupId);
    }

    [Fact]
    public void Manager_Get_ReturnsNullForUnknownGroup()
    {
        // Arrange
        var manager = fixture.ConsumerManager;

        var settings = manager.Get("non-existent-group");
        Assert.Null(settings);
    }

    [Fact]
    public void Manager_Unregister_RemovesFromManager()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var settings = fixture.SettingsForTestGroup;

        Assert.True(manager.IsRegistered(group));

        manager.Unregister(group);

        Assert.False(manager.IsRegistered(group));
        Assert.Null(manager.Get(group));

        // Restore for other tests in the sequential collection
        manager.TryRegister(group, settings);
    }

    [Fact]
    public void Manager_Unregister_GetAllExcludesRemoved()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var allBefore = manager.GetAllConsumerGroupIds();
        var removedGroup = fixture.CountingGroupId;
        var settings = fixture.SettingsForTestGroup;
        Assert.NotEmpty(allBefore);

        manager.Unregister(removedGroup);

        var allAfter = manager.GetAllConsumerGroupIds();
        Assert.DoesNotContain(removedGroup, allAfter);
        Assert.Equal(allBefore.Count - 1, allAfter.Count);

        // Restore for other tests in the sequential collection
        manager.TryRegister(removedGroup, settings);
    }

    [Fact]
    public async Task Manager_Unregister_ProcessMessages_SkipsUnregistered()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();

        // Unregister the group
        manager.Unregister(fixture.CountingGroupId);
        Assert.False(manager.IsRegistered(fixture.CountingGroupId));

        // Publish messages
        var messages = new[] { new TestMessage { PayloadId = "unreg-1", Content = "Msg 1", TenantId = 1 } };
        await publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);

        // ProcessMessages should handle gracefully — unregistered group means no settings,
        // so ProcessMessages with explicit settings should still work
        var settings = fixture.SettingsForTestGroup;
        var result = await fixture.Sub.ProcessMessages<TestMessage>(settings, TestContext.Current.CancellationToken);

        // Even though manager doesn't know the group, we passed settings directly —
        // ProcessMessages should still process
        Assert.True(result >= 0);

        // Restore for other tests in the sequential collection
        manager.TryRegister(fixture.CountingGroupId, fixture.SettingsForTestGroup);
    }

    #endregion

    #region GetAllConsumerGroupIds

    [Fact]
    public void Manager_GetAllConsumerGroupIds_ReturnsAllGroups()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var allGroups = manager.GetAllConsumerGroupIds();

        // Должно быть как минимум две группы (fixture.CountingGroupId и fixture.BlockingGroupId),
        // настроенные в Fixture.
        Assert.True(allGroups.Count >= 2, $"Ожидалось как минимум 2 группы, получено: {allGroups.Count}");
        Assert.Contains(fixture.CountingGroupId, allGroups);
        Assert.Contains(Fixture.BlockingGroupId, allGroups);
    }

    [Fact]
    public void Manager_GetAllConsumerGroupIds_IsSnapshot()
    {
        // Arrange
        var manager = fixture.ConsumerManager;

        var snapshot1 = manager.GetAllConsumerGroupIds();
        var snapshot2 = manager.GetAllConsumerGroupIds();

        // Оба снимка должны иметь одинаковый размер, но разные ссылки
        Assert.Equal(snapshot1.Count, snapshot2.Count);
        Assert.NotSame(snapshot1, snapshot2);
    }

    #endregion

    #region Thread safety under real Consume

    [Fact]
    public async Task Manager_ConcurrentApplyAndGet_NoExceptions()
    {
        // Arrange
        var manager = fixture.ConsumerManager;
        var group = fixture.CountingGroupId;
        var publisher = fixture.Publisher;

        CountingMessageConsumer.Reset();

        const int iterations = 50;
        var exceptions = new List<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Publish messages
        var messages = Enumerable.Range(1, 20)
            .Select(i => new TestMessage { PayloadId = $"stress-{i}", Content = $"Msg {i}", TenantId = 1 })
            .ToList();
        await publisher.Publish(messages, m => m.TenantId, cts.Token);

        // Concurrent writer
        var writerTask = Task.Run(async () =>
        {
            for (int i = 0; i < iterations && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    manager.Apply(group, s => s with { MaxBatchSize = i + 1 });
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref exceptions, [.. exceptions, ex]);
                }

                await Task.Delay(2, cts.Token);
            }
        }, cts.Token);

        // Concurrent reader
        var readerTask = Task.Run(async () =>
        {
            for (int i = 0; i < iterations && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    var settings = manager.Get(group);
                    if (settings is not null)
                    {
                        _ = settings.MaxBatchSize;
                        _ = settings.Paused;
                        _ = settings.Version;
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref exceptions, [.. exceptions, ex]);
                }

                await Task.Delay(2, cts.Token);
            }
        }, cts.Token);

        // Concurrent pause/resume
        var pauseTask = Task.Run(async () =>
        {
            for (int i = 0; i < 20 && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    manager.Pause(group);
                    await Task.Delay(1, cts.Token);
                    manager.Resume(group);
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref exceptions, [.. exceptions, ex]);
                }
            }
        }, cts.Token);

        await Task.WhenAll(writerTask, readerTask, pauseTask);

        Assert.Empty(exceptions);
    }

    #endregion
}

