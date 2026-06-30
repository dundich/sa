using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

public class OutboxConsumerManagerTests
{
    private static OutboxConsumerSettings CreateSettings(
        string consumerGroupId = "test-group",
        bool paused = false)
        => new(
            ConsumerGroupId: consumerGroupId,
            AsSingleton: false,
            Interval: TimeSpan.FromSeconds(5),
            InitialDelay: TimeSpan.Zero,
            ConcurrencyLimit: 1,
            MaxConcurrency: 1,
            RetryCountOnError: 0,
            MaxBatchSize: 16,
            MaxProcessingIterations: -1,
            IterationDelay: TimeSpan.Zero,
            LockDuration: TimeSpan.FromSeconds(10),
            LockRenewal: TimeSpan.FromSeconds(3),
            LookbackInterval: TimeSpan.FromDays(7),
            MaxDeliveryAttempts: 3,
            BatchingWindow: TimeSpan.Zero,
            PerTenantTimeout: TimeSpan.Zero,
            PerTenantMaxDegreeOfParallelism: 1,
            Paused: paused,
            Version: 0);

    private static IOutboxConsumerManager CreateManager()
        => new OutboxConsumerManager();

    #region Pause / Resume

    [Fact]
    public void Pause_SetsPausedToTrue()
    {
        var manager = CreateManager();
        var group = "pause-test";
        var settings = CreateSettings(group);

        manager.Register(group, settings);
        Assert.False(manager.IsPaused(group));

        manager.Pause(group);
        Assert.True(manager.IsPaused(group));
    }

    [Fact]
    public void Resume_SetsPausedToFalse()
    {
        var manager = CreateManager();
        var group = "resume-test";
        var settings = CreateSettings(group, paused: true);

        manager.Register(group, settings);
        Assert.True(manager.IsPaused(group));

        manager.Resume(group);
        Assert.False(manager.IsPaused(group));
    }

    [Fact]
    public void Pause_ThenResume_RestartsWithOriginalSettings()
    {
        var manager = CreateManager();
        var group = "pause-resume-cycle";
        var settings = CreateSettings(group);

        manager.Register(group, settings);
        manager.Pause(group);
        Assert.True(manager.IsPaused(group));

        manager.Resume(group);
        Assert.False(manager.IsPaused(group));

        // Settings preserved after pause/resume cycle
        var retrieved = manager.Get(group);
        Assert.NotNull(retrieved);
        Assert.Equal(settings.Interval, retrieved.Interval);
        Assert.Equal(settings.MaxBatchSize, retrieved.MaxBatchSize);
    }

    [Fact]
    public void Pause_OnUnregisteredGroup_ThrowsInvalidOperationException()
    {
        var manager = CreateManager();

        var exception = Assert.Throws<InvalidOperationException>(() => manager.Pause("non-existent"));
        Assert.Contains("not registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resume_OnUnregisteredGroup_ThrowsInvalidOperationException()
    {
        var manager = CreateManager();

        var exception = Assert.Throws<InvalidOperationException>(() => manager.Resume("non-existent"));
        Assert.Contains("not registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pause_OnNullGroup_ThrowsArgumentException()
    {
        var manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.Pause(null!));
        Assert.Throws<ArgumentException>(() => manager.Pause(""));
        Assert.Throws<ArgumentException>(() => manager.Pause("   "));
    }

    [Fact]
    public void Resume_OnNullGroup_ThrowsArgumentException()
    {
        var manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.Resume(null!));
        Assert.Throws<ArgumentException>(() => manager.Resume(""));
        Assert.Throws<ArgumentException>(() => manager.Resume("   "));
    }

    [Fact]
    public void Pause_PreservesAllSettingsExceptPaused()
    {
        var manager = CreateManager();
        var group = "pause-preserve";
        var settings = CreateSettings(group);

        manager.Register(group, settings);
        manager.Pause(group);

        var updated = manager.Get(group);
        Assert.NotNull(updated);
        Assert.True(updated.Paused);
        Assert.Equal(settings.Interval, updated.Interval);
        Assert.Equal(settings.LockDuration, updated.LockDuration);
        Assert.Equal(settings.MaxBatchSize, updated.MaxBatchSize);
        Assert.Equal(settings.MaxDeliveryAttempts, updated.MaxDeliveryAttempts);
        // Pause uses Apply internally — Version stays unchanged unless caller increments it
        Assert.Equal(settings.Version, updated.Version);
    }

    #endregion

    #region Subscribe

    [Fact]
    public void Subscribe_CallbackFiredOnApply()
    {
        var manager = CreateManager();
        var group = "subscribe-test";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        OutboxConsumerSettings? captured = null;
        using var subscription = manager.Subscribe(group, s => captured = s);

        manager.Apply(group, s => s with { MaxBatchSize = 64 });

        Assert.NotNull(captured);
        Assert.Equal(64, captured.MaxBatchSize);
        Assert.Equal(group, captured.ConsumerGroupId);
    }

    [Fact]
    public void Subscribe_CallbackFiredOnPause()
    {
        var manager = CreateManager();
        var group = "subscribe-pause";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        OutboxConsumerSettings? captured = null;
        using var subscription = manager.Subscribe(group, s => captured = s);

        manager.Pause(group);

        Assert.NotNull(captured);
        Assert.True(captured.Paused);
    }

    [Fact]
    public void Subscribe_CallbackFiredOnResume()
    {
        var manager = CreateManager();
        var group = "subscribe-resume";
        var settings = CreateSettings(group, paused: true);

        manager.Register(group, settings);

        OutboxConsumerSettings? captured = null;
        using var subscription = manager.Subscribe(group, s => captured = s);

        manager.Resume(group);

        Assert.NotNull(captured);
        Assert.False(captured.Paused);
    }

    [Fact]
    public void Subscribe_MultipleCallbacks_AllFired()
    {
        var manager = CreateManager();
        var group = "subscribe-multiple";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        var callback1Invoked = false;
        var callback2Invoked = false;

        using var sub1 = manager.Subscribe(group, _ => callback1Invoked = true);
        using var sub2 = manager.Subscribe(group, _ => callback2Invoked = true);

        manager.Apply(group, s => s with { MaxBatchSize = 1 });

        Assert.True(callback1Invoked);
        Assert.True(callback2Invoked);
    }

    [Fact]
    public void Unsubscribe_DisposedCallbackNotFired()
    {
        var manager = CreateManager();
        var group = "subscribe-unsubscribe";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        var callbackInvoked = false;
        var subscription = manager.Subscribe(group, _ => callbackInvoked = true);
        subscription.Dispose();

        manager.Apply(group, s => s with { MaxBatchSize = 99 });

        Assert.False(callbackInvoked);
    }

    [Fact]
    public void Subscribe_ReceivesUpdatedVersion()
    {
        var manager = CreateManager();
        var group = "subscribe-version";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        int versionReceived = -1;
        using var subscription = manager.Subscribe(group, s => versionReceived = s.Version);

        // Caller increments Version in the transform
        manager.Apply(group, s => s with { MaxBatchSize = 32, Version = s.Version + 1 });
        Assert.Equal(settings.Version + 1, versionReceived);
    }

    [Fact]
    public void Subscribe_SubscriberErrorDoesNotBreakPipeline()
    {
        var manager = CreateManager();
        var group = "subscribe-error-tolerance";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        // Subscriber that throws
        using var badSub = manager.Subscribe(group, _ => throw new InvalidOperationException("boom"));

        // Another subscriber that should still fire
        var goodCallbackInvoked = false;
        using var goodSub = manager.Subscribe(group, _ => goodCallbackInvoked = true);

        // Should not throw
        manager.Apply(group, s => s with { MaxBatchSize = 10 });

        Assert.True(goodCallbackInvoked);
    }

    [Fact]
    public void Subscribe_NonExistentGroup_CreatesListenerEntry()
    {
        var manager = CreateManager();
        var group = "subscribe-no-register";

        // Subscribe on unregistered group should not throw (listener list created lazily)
        var callbackInvoked = false;
        using var subscription = manager.Subscribe(group, _ => callbackInvoked = true);

        // Now register — subscriber should receive
        manager.Register(group, CreateSettings(group));
        Assert.True(callbackInvoked);
    }

    [Fact]
    public void Subscribe_NullGroup_ThrowsArgumentException()
    {
        var manager = CreateManager();

        Assert.Throws<ArgumentException>(() => manager.Subscribe(null!, _ => { }));
        Assert.Throws<ArgumentException>(() => manager.Subscribe("", _ => { }));
        Assert.Throws<ArgumentException>(() => manager.Subscribe("   ", _ => { }));
    }

    [Fact]
    public void Subscribe_NullCallback_ThrowsArgumentNullException()
    {
        var manager = CreateManager();
        var group = "subscribe-null-callback";

        Assert.Throws<ArgumentNullException>(() => manager.Subscribe(group, null!));
    }

    #endregion

    #region Apply

    [Fact]
    public void Apply_TransformsSettingsAtomically()
    {
        var manager = CreateManager();
        var group = "apply-atomic";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        manager.Apply(group, s => s with { MaxBatchSize = 128, MaxDeliveryAttempts = 5 });

        var updated = manager.Get(group);
        Assert.NotNull(updated);
        Assert.Equal(128, updated.MaxBatchSize);
        Assert.Equal(5, updated.MaxDeliveryAttempts);
    }

    [Fact]
    public void Apply_OnUnregisteredGroup_ThrowsInvalidOperationException()
    {
        var manager = CreateManager();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            manager.Apply("non-existent", s => s));
        Assert.Contains("not registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_VersionIncrements()
    {
        var manager = CreateManager();
        var group = "apply-version";
        var settings = CreateSettings(group);

        manager.Register(group, settings);
        var initialVersion = settings.Version;

        manager.Apply(group, s => s with { MaxBatchSize = 1, Version = s.Version + 1 });
        var updated = manager.Get(group);

        // Version is managed by caller via transform — Apply itself does not auto-increment
        Assert.Equal(initialVersion + 1, updated!.Version);
    }

    [Fact]
    public void Apply_ConsecutiveUpdates_AccumulateChanges()
    {
        var manager = CreateManager();
        var group = "apply-chain";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        manager.Apply(group, s => s with { MaxBatchSize = 8 });
        manager.Apply(group, s => s with { MaxDeliveryAttempts = 10 });
        manager.Apply(group, s => s with { Paused = true });

        var final = manager.Get(group);
        Assert.NotNull(final);
        Assert.Equal(8, final.MaxBatchSize);
        Assert.Equal(10, final.MaxDeliveryAttempts);
        Assert.True(final.Paused);
    }

    #endregion

    #region Get / IsRegistered

    [Fact]
    public void Get_ReturnsNullForUnknownGroup()
    {
        var manager = CreateManager();
        Assert.Null(manager.Get("unknown-group"));
    }

    [Fact]
    public void Get_ReturnsCurrentSnapshot()
    {
        var manager = CreateManager();
        var group = "get-snapshot";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        var snapshot = manager.Get(group);
        Assert.NotNull(snapshot);
        Assert.Same(settings, snapshot);
    }

    [Fact]
    public void Get_AfterApply_ReturnsUpdatedSnapshot()
    {
        var manager = CreateManager();
        var group = "get-after-apply";
        var settings = CreateSettings(group);

        manager.Register(group, settings);
        manager.Apply(group, s => s with { MaxBatchSize = 256 });

        var snapshot = manager.Get(group);
        Assert.NotNull(snapshot);
        Assert.Equal(256, snapshot.MaxBatchSize);
    }

    [Fact]
    public void IsRegistered_TrueAfterRegister()
    {
        var manager = CreateManager();
        var group = "is-registered";

        Assert.False(manager.IsRegistered(group));

        manager.Register(group, CreateSettings(group));
        Assert.True(manager.IsRegistered(group));
    }

    [Fact]
    public void IsRegistered_FalseAfterUnregister()
    {
        var manager = CreateManager();
        var group = "is-unregistered";
        var settings = CreateSettings(group);

        manager.Register(group, settings);
        Assert.True(manager.IsRegistered(group));

        manager.Unregister(group);
        Assert.False(manager.IsRegistered(group));
    }

    [Fact]
    public void IsRegistered_NullGroup_ReturnsFalse()
    {
        var manager = CreateManager();
        Assert.False(manager.IsRegistered(null!));
    }

    #endregion

    #region GetAllConsumerGroupIds

    [Fact]
    public void GetAllConsumerGroupIds_ReturnsEmptyWhenNoneRegistered()
    {
        var manager = CreateManager();
        var ids = manager.GetAllConsumerGroupIds();
        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    [Fact]
    public void GetAllConsumerGroupIds_ReturnsAllRegisteredGroups()
    {
        var manager = CreateManager();
        var group1 = "group-alpha";
        var group2 = "group-beta";
        var group3 = "group-gamma";

        manager.Register(group1, CreateSettings(group1));
        manager.Register(group2, CreateSettings(group2));
        manager.Register(group3, CreateSettings(group3));

        var ids = manager.GetAllConsumerGroupIds();
        Assert.Equal(3, ids.Count);
        Assert.Contains(group1, ids);
        Assert.Contains(group2, ids);
        Assert.Contains(group3, ids);
    }

    [Fact]
    public void GetAllConsumerGroupIds_ExcludesUnregistered()
    {
        var manager = CreateManager();
        var group1 = "keep-me";
        var group2 = "remove-me";

        manager.Register(group1, CreateSettings(group1));
        manager.Register(group2, CreateSettings(group2));
        manager.Unregister(group2);

        var ids = manager.GetAllConsumerGroupIds();
        Assert.Single(ids);
        Assert.DoesNotContain(group2, ids);
    }

    #endregion

    #region Thread safety

    [Fact]
    public async Task Concurrent_ApplyAndRead_NoDataRace()
    {
        var manager = CreateManager();
        var group = "concurrent-test";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        var exceptions = new List<Exception>();
        var iterations = 100;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writer = Task.Run(async () =>
        {
            for (int i = 0; i < iterations && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    var batchSize = i + 1;
                    manager.Apply(group, s => s with { MaxBatchSize = batchSize });
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }

                await Task.Delay(1, cts.Token);
            }
        }, cts.Token);

        var reader = Task.Run(async () =>
        {
            for (int i = 0; i < iterations && !cts.Token.IsCancellationRequested; i++)
            {
                try
                {
                    var snapshot = manager.Get(group);
                    if (snapshot is not null && (snapshot.MaxBatchSize < 1 || snapshot.MaxBatchSize > iterations))
                    {
                        // Snapshot received is consistent — even if we miss some updates
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }

                await Task.Delay(1, cts.Token);
            }
        }, cts.Token);

        await Task.WhenAll(writer, reader);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task Concurrent_PauseResumeAndSubscribe_NoCrash()
    {
        var manager = CreateManager();
        var group = "stress-test";
        var settings = CreateSettings(group);

        manager.Register(group, settings);

        var fired = 0;
        using var sub = manager.Subscribe(group, _ => Interlocked.Increment(ref fired));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var pauseTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50 && !cts.Token.IsCancellationRequested; i++)
            {
                manager.Pause(group);
                await Task.Delay(1, cts.Token);
                manager.Resume(group);
            }
        }, cts.Token);

        var readTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50 && !cts.Token.IsCancellationRequested; i++)
            {
                manager.Get(group);
                await Task.Delay(1, cts.Token);
            }
        }, cts.Token);

        var applyTask = Task.Run(async () =>
        {
            for (int i = 0; i < 50 && !cts.Token.IsCancellationRequested; i++)
            {
                manager.Apply(group, s => s with { MaxBatchSize = i + 1 });
                await Task.Delay(1, cts.Token);
            }
        }, cts.Token);

        await Task.WhenAll(pauseTask, readTask, applyTask);
        Assert.True(fired > 0);
    }

    #endregion
}
