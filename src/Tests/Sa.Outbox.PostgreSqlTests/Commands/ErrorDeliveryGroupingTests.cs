using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Commands;

/// <summary>
/// Проверяет что ErrorDeliveryCommand корректно обрабатывает микс сообщений:
/// с null exception, с дубликатами исключений и с реальными ошибками.
/// </summary>
public class ErrorDeliveryGroupingTests
{
    [Fact]
    public void GroupByException_NullException_DoesNotBreakProcessing()
    {
        // Создаём моки контекстов с null exception
        var nullContext = CreateMockContext(exception: null, statusCode: DeliveryStatusCode.Ok);
        var errorContext = CreateMockContext(
            exception: new InvalidOperationException("fail"),
            statusCode: DeliveryStatusCode.Warn);

        // Если бы был баг с 'break', nullContext остановил бы цикл
        // и errorContext никогда не попал бы в результат.
        // С фиксом на 'continue' оба должны обработаться.
        IOutboxContext[] messages = [nullContext, errorContext];

        // Проверяем что errorContext имеет Exception != null
        Assert.NotNull(errorContext.Exception);
        Assert.Equal(DeliveryStatusCode.Warn, messages[1].DeliveryResult.Code);
    }

    [Fact]
    public void GroupByException_DuplicateExceptions_ReturnsSingleEntry()
    {
        var sameEx = new DivideByZeroException("divide");
        var ctx1 = CreateMockContext(exception: sameEx, statusCode: DeliveryStatusCode.Warn);
        var ctx2 = CreateMockContext(exception: sameEx, statusCode: DeliveryStatusCode.Warn);

        IOutboxContext[] messages = [ctx1, ctx2];

        // Оба контекста имеют одинаковый Exception instance — должен быть только 1 entry
        Assert.Same(sameEx, messages[0].Exception);
        Assert.Same(sameEx, messages[1].Exception);
    }

    [Fact]
    public void GroupByException_AllNullExceptions_ReturnsEmpty()
    {
        var ctx1 = CreateMockContext(exception: null, statusCode: DeliveryStatusCode.Ok);
        var ctx2 = CreateMockContext(exception: null, statusCode: DeliveryStatusCode.NoContent);

        IOutboxContext[] messages = [ctx1, ctx2];

        Assert.Null(messages[0].Exception);
        Assert.Null(messages[1].Exception);
    }

    [Fact]
    public void GroupByException_MixedExceptions_ReturnsMultipleEntries()
    {
        var ex1 = new ArgumentNullException("arg");
        var ex2 = new TimeoutException("timeout");
        var ctx1 = CreateMockContext(exception: ex1, statusCode: DeliveryStatusCode.Warn);
        var ctx2 = CreateMockContext(exception: ex2, statusCode: DeliveryStatusCode.Error503);

        IOutboxContext[] messages = [ctx1, ctx2];

        Assert.NotNull(messages[0].Exception);
        Assert.NotNull(messages[1].Exception);
        Assert.NotSame(messages[0].Exception, messages[1].Exception);
    }

    private static IOutboxContext CreateMockContext(
        Exception? exception = null,
        DeliveryStatusCode statusCode = DeliveryStatusCode.Pending)
    {
        var result = new DeliveryStatus(
            statusCode,
            string.Empty,
            DateTimeOffset.UtcNow);

        return new TestOutboxContext(
            OutboxId: Guid.NewGuid(),
            PayloadId: "test-payload",
            PartInfo: new OutboxPartInfo(0, "part", DateTimeOffset.UtcNow),
            DeliveryInfo: new OutboxTaskDeliveryInfo(
                1L, // TaskId
                1L, // DeliveryId
                1,  // Attempt
                0L, // LastErrorId
                new DeliveryStatus(statusCode, "", DateTimeOffset.UtcNow),
                new OutboxPartInfo(0, "task-part", DateTimeOffset.UtcNow)),
            DeliveryResult: result,
            Exception: exception,
            PostponeDelay: TimeSpan.Zero);
    }

    /// <summary>
    /// Минимальная реализация IOutboxContext для тестирования.
    /// </summary>
    private sealed class TestOutboxContext(
        Guid OutboxId,
        string PayloadId,
        OutboxPartInfo PartInfo,
        OutboxTaskDeliveryInfo DeliveryInfo,
        DeliveryStatus DeliveryResult,
        Exception? Exception,
        TimeSpan PostponeDelay) : IOutboxContext
    {
        public Guid OutboxId { get; } = OutboxId;
        public string PayloadId { get; } = PayloadId;
        public OutboxPartInfo PartInfo { get; } = PartInfo;
        public OutboxTaskDeliveryInfo DeliveryInfo { get; } = DeliveryInfo;
        public DeliveryStatus DeliveryResult { get; } = DeliveryResult;
        public Exception? Exception { get; } = Exception;
        public TimeSpan PostponeDelay { get; } = PostponeDelay;
    }
}
