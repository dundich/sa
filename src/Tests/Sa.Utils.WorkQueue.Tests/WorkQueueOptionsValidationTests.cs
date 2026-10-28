namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Regression tests for <see cref="SaWorkQueue{TInput}"/> constructor
/// validation (B8). The options record has a public primary constructor, so
/// its arguments bypass the <c>With*</c> guards entirely — the queue
/// constructor re-validates them so an invalid value fails fast (and the
/// same way) no matter how the options were assembled. Before the fix,
/// a negative <c>QueueCapacity</c> threw deep inside
/// <see cref="System.Threading.Channels.Channel{T}.CreateBounded"/> and a
/// negative <c>ConcurrencyLimit</c> silently paused the queue to 0.
/// </summary>
public sealed class WorkQueueOptionsValidationTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private sealed class CountingProcessor : ISaWork<int>
    {
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public Task Execute(int input, CancellationToken ct)
        {
            Interlocked.Increment(ref _processed);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Ctor_NegativeQueueCapacity_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SaWorkQueue<int>(new SaWorkQueueOptions<int>(new CountingProcessor(), QueueCapacity: -5)));

        Assert.Equal(nameof(SaWorkQueueOptions<int>.QueueCapacity), ex.ParamName);
    }

    [Fact]
    public void Ctor_ZeroQueueCapacity_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SaWorkQueue<int>(new SaWorkQueueOptions<int>(new CountingProcessor(), QueueCapacity: 0)));

        Assert.Equal(nameof(SaWorkQueueOptions<int>.QueueCapacity), ex.ParamName);
    }

    [Fact]
    public void Ctor_NegativeConcurrencyLimit_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SaWorkQueue<int>(new SaWorkQueueOptions<int>(new CountingProcessor(), ConcurrencyLimit: -3)));

        Assert.Equal(nameof(SaWorkQueueOptions<int>.ConcurrencyLimit), ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_NonPositiveShutdownTimeout_Throws(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new SaWorkQueue<int>(new SaWorkQueueOptions<int>(new CountingProcessor(),
                ShutdownTimeout: TimeSpan.FromMilliseconds(milliseconds))));

        Assert.Equal(nameof(SaWorkQueueOptions<int>.ShutdownTimeout), ex.ParamName);
    }

    [Fact]
    public void WithMaxConcurrency_Negative_Throws()
    {
        // Единственный With*-метод без проверки знака раньше: -1 молча сворачивался в число
        // CPU, то есть опечатка в знаке выглядела как осознанное значение по умолчанию —
        // ровно то, из-за чего конструктор очереди отвергает отрицательный MaxConcurrency.
        var options = SaWorkQueueOptions<int>.Create(new CountingProcessor());

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.WithMaxConcurrency(-1));
        Assert.Equal("limit", ex.ParamName);

        var ex2 = Assert.Throws<ArgumentOutOfRangeException>(() => options.WithMaxConcurrency(int.MinValue));
        Assert.Equal("limit", ex2.ParamName);
    }

    [Fact]
    public void WithMaxConcurrency_Zero_MeansProcessorCount()
    {
        var options = SaWorkQueueOptions<int>.Create(new CountingProcessor()).WithMaxConcurrency(0);

        Assert.Equal(Environment.ProcessorCount, options.MaxConcurrency);
    }

    [Fact]
    public async Task Ctor_BoundaryValidValues_AcceptedAndWork()
    {
        // The minimum acceptable values must not just pass validation —
        // the queue built from them has to be fully functional.
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(new SaWorkQueueOptions<int>(
            processor,
            QueueCapacity: 1,
            ConcurrencyLimit: 0, // valid: starts paused
            ShutdownTimeout: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(0, queue.ConcurrencyLimit);
        Assert.Equal(1, queue.QueueCapacity);
        Assert.True(queue.IsIdle());

        queue.ConcurrencyLimit = 1; // resume
        await queue.Enqueue(7, TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.True(queue.IsIdle());
        Assert.Equal(1, processor.Processed);
    }

    /// <summary>
    /// Undefined values of the three strategy enums are rejected, exactly as the integer
    /// options are: the queue reads them through equality tests and switch arms, so an
    /// undefined value did not fail — it silently became a real one (Skip, Hard, Lifo),
    /// turning a configuration typo into a deliberate-looking choice.
    /// </summary>
    [Fact]
    public void Ctor_UndefinedStrategyEnums_Throw()
    {
        var enqueue = Assert.Throws<ArgumentOutOfRangeException>(() => new SaWorkQueue<int>(
            new SaWorkQueueOptions<int>(new CountingProcessor(), EnqueueStrategy: (SaEnqueueStrategy)99)));
        Assert.Equal(nameof(SaWorkQueueOptions<int>.EnqueueStrategy), enqueue.ParamName);

        var mode = Assert.Throws<ArgumentOutOfRangeException>(() => new SaWorkQueue<int>(
            new SaWorkQueueOptions<int>(new CountingProcessor(), ReaderCancelMode: (SaReaderCancelMode)99)));
        Assert.Equal(nameof(SaWorkQueueOptions<int>.ReaderCancelMode), mode.ParamName);

        var order = Assert.Throws<ArgumentOutOfRangeException>(() => new SaWorkQueue<int>(
            new SaWorkQueueOptions<int>(new CountingProcessor(), ReaderCancellationOrder: (SaReaderCancellationOrder)99)));
        Assert.Equal(nameof(SaWorkQueueOptions<int>.ReaderCancellationOrder), order.ParamName);
    }

    [Fact]
    public void Ctor_DefinedStrategyEnums_AreAccepted()
    {
        // The guard must be about *defined* values, not about one blessed combination.
        using var queue = new SaWorkQueue<int>(new SaWorkQueueOptions<int>(
            new CountingProcessor(),
            EnqueueStrategy: SaEnqueueStrategy.Throw,
            ReaderCancelMode: SaReaderCancelMode.Soft,
            ReaderCancellationOrder: SaReaderCancellationOrder.Random));

        Assert.True(queue.IsEnabled);
    }
}
