using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

public class DeliveryCourierTests
{
    private sealed class TestMessage { }

    private static OutboxConsumerSettings CreateSettings(int maxDeliveryAttempts = 3)
        => new(Id: Guid.NewGuid(), "test-group", AsSingleton: false, Interval: TimeSpan.FromMinutes(1), InitialDelay: TimeSpan.Zero,
            ConcurrencyLimit: 1, MaxConcurrency: 1, RetryCountOnError: 0,
            MaxBatchSize: 16, MaxProcessingIterations: -1, IterationDelay: TimeSpan.Zero,
            LockDuration: TimeSpan.FromSeconds(10), LockRenewal: TimeSpan.FromSeconds(3),
            LookbackInterval: TimeSpan.FromDays(7), MaxDeliveryAttempts: maxDeliveryAttempts,
            BatchingWindow: TimeSpan.FromSeconds(3), PerTenantTimeout: TimeSpan.Zero,
            PerTenantMaxDegreeOfParallelism: 1, Paused: false, Version: 0);

    private static OutboxMessageFilter CreateFilter()
        => new(
            "txn-1",
            "test-group",
            "Sa.Outbox.Tests.DeliveryCourierTests+TestMessage",
            1,
            "part-1",
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            DateTimeOffset.UtcNow);

    private static ReadOnlyMemory<IOutboxContextOperations<TestMessage>> ToMessages(params FakeOutboxContext<TestMessage>[] contexts)
        => new(contexts);

    #region Empty batch tests

    [Fact]
    public async Task Deliver_EmptyBatch_ReturnsZero()
    {
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver<TestMessage>(
            CreateSettings(),
            CreateFilter(),
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>>.Empty,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Empty(processor.Invocations);
    }

    #endregion

    #region Processor succeeds — all messages OK

    [Fact]
    public async Task Deliver_ProcessorSucceeds_AllMessagesOk_ReturnsSuccessCount()
    {
        var ctx1 = new FakeOutboxContext<TestMessage>(payloadId: "msg-1");
        var ctx2 = new FakeOutboxContext<TestMessage>(payloadId: "msg-2");
        var ctx3 = new FakeOutboxContext<TestMessage>(payloadId: "msg-3");
        var messages = ToMessages(ctx1, ctx2, ctx3);

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(3, result);
        Assert.Equal(DeliveryStatusCode.Ok, ctx1.DeliveryResult.Code);
        Assert.Equal(DeliveryStatusCode.Ok, ctx2.DeliveryResult.Code);
        Assert.Equal(DeliveryStatusCode.Ok, ctx3.DeliveryResult.Code);
    }

    [Fact]
    public async Task Deliver_ProcessorSucceeds_SingleMessage_ReturnsOne()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "single-msg");
        var messages = ToMessages(ctx);

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(DeliveryStatusCode.Ok, ctx.DeliveryResult.Code);
    }

    #endregion

    #region Processor throws — messages get postponed with retry backoff

    [Fact]
    public async Task Deliver_ProcessorThrows_MessageWarnedWithBackoff()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "msg-fail", attempt: 0);
        var messages = ToMessages(ctx);

        var testException = new InvalidOperationException("processor broke");
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromException(testException));

        var retryStrategy = new FakeRetryStrategy(_ => TimeSpan.FromSeconds(5));
        var courier = new DeliveryCourier(processor, retryStrategy);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(DeliveryStatusCode.Warn, ctx.DeliveryResult.Code);
        Assert.Same(testException, ctx.Exception);
        Assert.Equal(TimeSpan.FromSeconds(5), ctx.PostponeAt);
    }

    [Fact]
    public async Task Deliver_ProcessorThrows_MultipleMessagesAllWarned()
    {
        var ctx1 = new FakeOutboxContext<TestMessage>(payloadId: "msg-1", attempt: 0);
        var ctx2 = new FakeOutboxContext<TestMessage>(payloadId: "msg-2", attempt: 1);
        var ctx3 = new FakeOutboxContext<TestMessage>(payloadId: "msg-3", attempt: 2);
        var messages = ToMessages(ctx1, ctx2, ctx3);

        var testException = new TimeoutException("timeout");
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromException(testException));

        var callCount = 0;
        var expectedBackoffs = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) };
        var retryStrategy = new FakeRetryStrategy(attempt =>
        {
            var backoff = expectedBackoffs[callCount++];
            return backoff;
        });

        var courier = new DeliveryCourier(processor, retryStrategy);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(DeliveryStatusCode.Warn, ctx1.DeliveryResult.Code);
        Assert.Equal(DeliveryStatusCode.Warn, ctx2.DeliveryResult.Code);
        Assert.Equal(DeliveryStatusCode.Warn, ctx3.DeliveryResult.Code);
        Assert.Equal(expectedBackoffs[0], ctx1.PostponeAt);
        Assert.Equal(expectedBackoffs[1], ctx2.PostponeAt);
        Assert.Equal(expectedBackoffs[2], ctx3.PostponeAt);
        Assert.Same(testException, ctx1.Exception);
        Assert.Same(testException, ctx2.Exception);
        Assert.Same(testException, ctx3.Exception);
    }

    [Fact]
    public async Task Deliver_ProcessorThrows_UseDefaultRetryStrategy()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "msg-default-retry", attempt: 1);
        var messages = ToMessages(ctx);

        var testException = new InvalidOperationException("fail");
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromException(testException));

        // No custom retry strategy — should use ExponentialBackoffRetryStrategy.Shared
        var courier = new DeliveryCourier(processor, null);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(DeliveryStatusCode.Warn, ctx.DeliveryResult.Code);
        Assert.NotNull(ctx.Exception);
        Assert.True(ctx.PostponeAt > TimeSpan.Zero);
    }

    #endregion


    #region Pre-existing warning states

    [Fact]
    public async Task Deliver_AlreadyWarning_BelowMaxAttempts_Skipped()
    {
        var ctx = new FakeOutboxContext<TestMessage>(
            payloadId: "msg-below-max",
            attempt: 0,
            initialStatus: DeliveryStatusCode.Warn);
        var messages = ToMessages(ctx);

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver(
            CreateSettings(maxDeliveryAttempts: 3),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.NotEqual(DeliveryStatusCode.MaximumAttemptsError, ctx.DeliveryResult.Code);
        Assert.NotEqual(DeliveryStatusCode.Ok, ctx.DeliveryResult.Code);
    }

    [Fact]
    public async Task Deliver_AlreadyWarning_TrulyExceedsMaxAttempts_ErrorMaxAttemptsCalled()
    {
        var ctx = new FakeOutboxContext<TestMessage>(
            payloadId: "msg-max-attempts",
            attempt: 3, // attempt(3) + 1 = 4 > max(3) ✓
            initialStatus: DeliveryStatusCode.Warn);
        var messages = ToMessages(ctx);

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver(
            CreateSettings(maxDeliveryAttempts: 3),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(DeliveryStatusCode.MaximumAttemptsError, ctx.DeliveryResult.Code);
    }

    [Fact]
    public async Task Deliver_AlreadySuccess_Skipped()
    {
        var ctx = new FakeOutboxContext<TestMessage>(
            payloadId: "msg-done",
            attempt: 0,
            initialStatus: DeliveryStatusCode.Ok);
        var messages = ToMessages(ctx);

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(DeliveryStatusCode.Ok, ctx.DeliveryResult.Code);
    }

    [Fact]
    public async Task Deliver_AlreadyProcessing_Skipped()
    {
        var ctx = new FakeOutboxContext<TestMessage>(
            payloadId: "msg-processing",
            attempt: 0,
            initialStatus: DeliveryStatusCode.Processing);
        var messages = ToMessages(ctx);

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    #endregion

    #region Mixed batch — some succeed, some fail

    [Fact]
    public async Task Deliver_MixedBatch_ProcessorSucceeds_PartialSuccess()
    {
        var ctxOk = new FakeOutboxContext<TestMessage>(payloadId: "msg-ok", attempt: 0);
        var ctxDone = new FakeOutboxContext<TestMessage>(
            payloadId: "msg-done",
            attempt: 0,
            initialStatus: DeliveryStatusCode.Ok);
        var ctxPending = new FakeOutboxContext<TestMessage>(
            payloadId: "msg-pending",
            attempt: 0,
            initialStatus: DeliveryStatusCode.Pending);

        var messages = ToMessages(ctxOk, ctxDone, ctxPending);

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.CompletedTask);
        var courier = new DeliveryCourier(processor);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        // msg-ok and msg-pending become Ok (2 successes), msg-done stays Ok but not counted
        Assert.Equal(2, result);
        Assert.Equal(DeliveryStatusCode.Ok, ctxOk.DeliveryResult.Code);
        Assert.Equal(DeliveryStatusCode.Ok, ctxDone.DeliveryResult.Code);
        Assert.Equal(DeliveryStatusCode.Ok, ctxPending.DeliveryResult.Code);
    }

    #endregion

    #region Retry strategy integration

    [Fact]
    public async Task Deliver_ProcessorThrows_CustomStrategyUsedNotShared()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "custom-strategy", attempt: 5);
        var messages = ToMessages(ctx);

        var testException = new InvalidOperationException("fail");
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromException(testException));

        var customStrategy = new FakeRetryStrategy(_ => TimeSpan.FromMilliseconds(42));
        var courier = new DeliveryCourier(processor, customStrategy);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Deliver_ProcessorThrows_BackoffBasedOnCurrentAttempt()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "attempt-aware", attempt: 0);
        var messages = ToMessages(ctx);

        var testException = new InvalidOperationException("fail");
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromException(testException));

        var recordedAttempts = new List<int>();
        var retryStrategy = new FakeRetryStrategy(attempt =>
        {
            recordedAttempts.Add(attempt);
            return TimeSpan.FromSeconds(attempt * 5);
        });

        var courier = new DeliveryCourier(processor, retryStrategy);

        await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Single(recordedAttempts);
        Assert.Equal(1, recordedAttempts[0]); // attempt 0 + 1 = 1
        Assert.Equal(TimeSpan.FromSeconds(5), ctx.PostponeAt);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task Deliver_ProcessorCancelled_TreatedAsRegularError()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "cancelled");
        var messages = ToMessages(ctx);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromCanceled(cts.Token));

        var retryStrategy = new FakeRetryStrategy(_ => TimeSpan.FromSeconds(1));
        var courier = new DeliveryCourier(processor, retryStrategy);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            cts.Token);

        Assert.Equal(0, result);
        Assert.Equal(DeliveryStatusCode.Warn, ctx.DeliveryResult.Code);
    }

    #endregion

    #region Deterministic retry strategy (no jitter)

    [Fact]
    public async Task Deliver_ProcessorThrows_DeterministicStrategy_PredictableDelays()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "det-strategy", attempt: 0);
        var messages = ToMessages(ctx);

        var testException = new InvalidOperationException("fail");
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromException(testException));

        // Strategy that always returns exactly 1 second regardless of attempt
        var deterministicStrategy = new FakeRetryStrategy(_ => TimeSpan.FromSeconds(1));
        var courier = new DeliveryCourier(processor, deterministicStrategy);

        var result = await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(TimeSpan.FromSeconds(1), ctx.PostponeAt);
    }

    #endregion

    #region Exception types preserved

    [Fact]
    public async Task Deliver_ProcessorThrows_PreservesOriginalExceptionType()
    {
        var ctx = new FakeOutboxContext<TestMessage>(payloadId: "exception-type");
        var messages = ToMessages(ctx);

        var testException = new CustomTestException("specific error");
        var processor = new FakeDeliveryLifetimeInvoker(_ => Task.FromException(testException));

        var retryStrategy = new FakeRetryStrategy(_ => TimeSpan.Zero);
        var courier = new DeliveryCourier(processor, retryStrategy);

        await courier.Deliver(
            CreateSettings(),
            CreateFilter(),
            messages,
            CancellationToken.None);

        Assert.IsType<CustomTestException>(ctx.Exception);
        Assert.Equal("specific error", ctx.Exception!.Message);
    }

    public sealed class CustomTestException(string message) : Exception(message);

    #endregion
}
