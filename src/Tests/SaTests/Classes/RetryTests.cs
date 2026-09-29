using Sa.Classes;
using Sa.Extensions;

namespace SaTests.Classes;

/// <summary>
/// Thrown by test stubs to simulate transient/retriable failures without pulling in external deps.
/// </summary>
public class TransientException : Exception
{
    public TransientException() : base()
    {
    }

    public TransientException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown by test stubs to simulate non-transient (terminal) failures.
/// </summary>
public class NonTransientException(string message) : Exception(message);

public class RetryTests
{
    #region Constant strategy

    [Fact]
    public async Task Constant_SuccessOnThirdAttempt_ReturnsInput()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            if (attempts < 3)
                throw new TransientException("fail");
            return new(input);
        }

        // Act
        int result = await Retry.Constant(
            Func,
            42,
            retryCount: 3,
            waitTime: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Constant_ExhaustRetries_ThrowsLastException()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            throw new TransientException("always fails");
        }

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TransientException>(() =>
            Retry.Constant(Func, 42, retryCount: 3, waitTime: 1, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("always fails", ex.Message);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(0)]   // zero retries → single call, no delay
    [InlineData(1)]   // one retry
    [InlineData(5)]   // many retries
    public async Task Constant_VaryingRetryCounts_CallCountMatches(int retryCount)
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            return new(input);
        }

        // Act
        int result = await Retry.Constant(
            Func,
            99,
            retryCount: retryCount,
            waitTime: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(99, result);
        Assert.Equal(1, attempts); // succeeds on first try regardless of retryCount
    }

    [Fact]
    public async Task Constant_NoInput_SuccessOnFirstCall()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(CancellationToken ct)
        {
            attempts++;
            return new(777);
        }

        // Act
        int result = await Retry.Constant(
            Func,
            retryCount: 3,
            waitTime: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(777, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Constant_NoInput_ExhaustsRetries()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(CancellationToken ct)
        {
            attempts++;
            throw new TransientException("no-input fail");
        }

        // Act & Assert
        var _ = await Assert.ThrowsAsync<TransientException>(() =>
            Retry.Constant(Func, retryCount: 2, waitTime: 1, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, attempts);
    }

    #endregion

    #region Linear strategy

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Linear_SuccessAfterFailures_ReturnsInput(int retryCount)
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            if (attempts == retryCount)
                return new(input);
            throw new TransientException("fail");
        }

        // Act
        int result = await Retry.Linear(
            Func,
            42,
            retryCount: retryCount,
            initialDelay: 10,
            factor: 1.0,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(retryCount, attempts);
    }

    [Fact]
    public async Task Linear_NoInput_ThrowsAfterExhaustion()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(CancellationToken ct)
        {
            attempts++;
            throw new TransientException("linear fail");
        }

        // Act & Assert
        await Assert.ThrowsAsync<TransientException>(() =>
            Retry.Linear(Func, retryCount: 3, initialDelay: 10, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(3, attempts);
    }

    #endregion

    #region Exponential strategy

    [Theory]
    [InlineData(3, 2.0)]
    [InlineData(4, 1.5)]
    public async Task Exponential_SuccessAfterFailures_ReturnsInput(int retryCount, double factor)
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            if (attempts == retryCount)
                return new(input);
            throw new TransientException("fail");
        }

        // Act
        int result = await Retry.Exponential(
            Func,
            42,
            retryCount: retryCount,
            initialDelay: 10,
            factor: factor,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(retryCount, attempts);
    }

    [Fact]
    public async Task Exponential_NoInput_ThrowsAfterExhaustion()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(CancellationToken ct)
        {
            attempts++;
            throw new TransientException("exp fail");
        }

        // Act & Assert
        await Assert.ThrowsAsync<TransientException>(() =>
            Retry.Exponential(Func, retryCount: 2, initialDelay: 10, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, attempts);
    }

    #endregion

    #region Jitter strategy

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Jitter_SuccessAfterFailures_ReturnsInput(int retryCount)
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            if (attempts == retryCount)
                return new(input);
            throw new TransientException("fail");
        }

        // Act
        int result = await Retry.Jitter(
            Func,
            42,
            retryCount: retryCount,
            medianFirstRetryDelay: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(retryCount, attempts);
    }

    [Fact]
    public async Task Jitter_NoInput_ThrowsAfterExhaustion()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(CancellationToken ct)
        {
            attempts++;
            throw new TransientException("jitter fail");
        }

        // Act & Assert
        await Assert.ThrowsAsync<TransientException>(() =>
            Retry.Jitter(Func, retryCount: 3, medianFirstRetryDelay: 10, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(3, attempts);
    }

    #endregion

    #region shouldRetry predicate

    [Fact]
    public async Task Constant_shouldRetry_True_ResumesUntilSuccess()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            if (attempts == 3)
                return new(input);
            throw new TransientException("transient");
        }

        // Act
        int result = await Retry.Constant(
            Func,
            42,
            retryCount: 5,
            waitTime: 1,
            shouldRetry: (ex, _) => ex is TransientException,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Constant_shouldRetry_False_StopsImmediately()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            throw new NonTransientException("terminal");
        }

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NonTransientException>(() =>
            Retry.Constant(
                Func,
                42,
                retryCount: 5,
                waitTime: 1,
                shouldRetry: (ex, _) => ex is TransientException,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("terminal", ex.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WaitAndRetry_shouldRetry_False_StopsImmediately()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            throw new NonTransientException("terminal");
        }

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NonTransientException>(() =>
            Retry.WaitAndRetry(
                [],
                Func,
                42,
                shouldRetry: (ex, _) => ex is TransientException,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("terminal", ex.Message);
        Assert.Equal(1, attempts);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task Constant_PreCancelledToken_ThrowsOCEWithoutCallingFunc()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            return new(input);
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        var _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Retry.Constant(Func, 42, retryCount: 3, waitTime: 1, cancellationToken: cts.Token).AsTask());

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Constant_DuringWait_CancelsAndThrowsLastException()
    {
        // Arrange
        int attempts = 0;
        var gate = new TaskCompletionSource();
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            if (attempts == 1)
                gate.SetResult();
            throw new TransientException("always fails");
        }

        using var cts = new CancellationTokenSource();
        var task = Retry.Constant(Func, 42, retryCount: 3, waitTime: 500, cancellationToken: cts.Token);

        await gate.Task;
        cts.Cancel();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<TransientException>(() => task.AsTask());
        Assert.Equal("always fails", ex.Message);
        Assert.True(attempts > 1, $"Expected more than 1 attempt after cancellation, got {attempts}");
    }

    #endregion

    #region Fatal exceptions

    [Fact]
    public async Task Constant_FatalOutOfMemory_ReThrowImmediately()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            throw new OutOfMemoryException();
        }

        // Act & Assert
        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            Retry.Constant(Func, 42, retryCount: 3, waitTime: 1, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Constant_FatalStackOverflow_ReThrowImmediately()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            throw new StackOverflowException();
        }

        // Act & Assert
        await Assert.ThrowsAsync<StackOverflowException>(() =>
            Retry.Constant(Func, 42, retryCount: 3, waitTime: 1, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Constant_FatalInvalidProgram_ReThrowImmediately()
    {
        // Arrange
        int attempts = 0;
        ValueTask<int> Func(int input, CancellationToken ct)
        {
            attempts++;
            throw new InvalidProgramException();
        }

        // Act & Assert
        await Assert.ThrowsAsync<InvalidProgramException>(() =>
            Retry.Constant(Func, 42, retryCount: 3, waitTime: 1, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException), true)]
    [InlineData(typeof(TransientException), false)]
    public void IsFatal_ReturnsExpected(Type exceptionType, bool expected)
    {
        // Arrange / Act
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        var actual = Retry.IsFatal(ex);

        // Assert
        Assert.Equal(expected, actual);
    }

    #endregion

    #region Quartz generators

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void GenerateConstant_CountMatchesRetryCount(int retryCount)
    {
        // Act
        var delays = Retry.Quartz.GenerateConstant(TimeSpan.FromMilliseconds(100), retryCount, fastFirst: true).ToList();

        // Assert
        Assert.Equal(retryCount, delays.Count);
    }

    [Fact]
    public void GenerateConstant_FirstElement_Zero_WhenFastFirst()
    {
        // Act
        var delays = Retry.Quartz.GenerateConstant(TimeSpan.FromMilliseconds(500), 3, fastFirst: true).ToList();

        // Assert
        Assert.Equal(TimeSpan.Zero, delays[0]);
        for (int i = 1; i < delays.Count; i++)
            Assert.Equal(TimeSpan.FromMilliseconds(500), delays[i]);
    }

    [Fact]
    public void GenerateConstant_NoFastFirst_NoZero()
    {
        // Act
        var delays = Retry.Quartz.GenerateConstant(TimeSpan.FromMilliseconds(500), 3, fastFirst: false).ToList();

        // Assert
        Assert.NotEqual(TimeSpan.Zero, delays[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void GenerateLinear_CountMatchesRetryCount(int retryCount)
    {
        // Act
        var delays = Retry.Quartz.GenerateLinear(TimeSpan.FromMilliseconds(100), retryCount, factor: 1.0, fastFirst: true).ToList();

        // Assert
        Assert.Equal(retryCount, delays.Count);
    }

    [Fact]
    public void GenerateLinear_IncreasesLinearly()
    {
        // Act
        var delays = Retry.Quartz.GenerateLinear(TimeSpan.FromMilliseconds(100), 4, factor: 1.0, fastFirst: true).ToList();

        // Assert
        Assert.Equal(TimeSpan.Zero, delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delays[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), delays[2]);
        Assert.Equal(TimeSpan.FromMilliseconds(300), delays[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void GenerateExponential_CountMatchesRetryCount(int retryCount)
    {
        // Act
        var delays = Retry.Quartz.GenerateExponential(TimeSpan.FromMilliseconds(100), retryCount, factor: 2.0, fastFirst: true).ToList();

        // Assert
        Assert.Equal(retryCount, delays.Count);
    }

    [Fact]
    public void GenerateExponential_GrowsByFactor()
    {
        // Act
        var delays = Retry.Quartz.GenerateExponential(TimeSpan.FromMilliseconds(100), 4, factor: 2.0, fastFirst: true).ToList();

        // Assert
        Assert.Equal(TimeSpan.Zero, delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delays[1]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), delays[2]);
        Assert.Equal(TimeSpan.FromMilliseconds(400), delays[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void GenerateJitter_CountMatchesRetryCount(int retryCount)
    {
        // Act
        var delays = Retry.Quartz.GenerateJitter(TimeSpan.FromMilliseconds(530), retryCount, fastFirst: true).ToList();

        // Assert
        Assert.Equal(retryCount, delays.Count);
    }

    [Fact]
    public void GenerateJitter_AllPositiveExceptFirst()
    {
        // Act
        var delays = Retry.Quartz.GenerateJitter(TimeSpan.FromMilliseconds(530), 5, fastFirst: true).ToList();

        // Assert
        Assert.Equal(TimeSpan.Zero, delays[0]);
        for (int i = 1; i < delays.Count; i++)
            Assert.True(delays[i] > TimeSpan.Zero, $"delay[{i}] should be positive");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void GenerateConstant_NegativeDelay_ThrowsArgumentOutOfRangeException(int ms)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Retry.Quartz.GenerateConstant(TimeSpan.FromMilliseconds(ms), 3));
        Assert.Equal("delay", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    public void GenerateConstant_NegativeRetryCount_ThrowsArgumentOutOfRangeException(int count)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Retry.Quartz.GenerateConstant(TimeSpan.FromMilliseconds(100), count));
        Assert.Equal("retryCount", ex.ParamName);
    }

    [Theory]
    [InlineData(0.5)]
    public void GenerateExponential_FactorLessThanOne_ThrowsArgumentOutOfRangeException(double factor)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Retry.Quartz.GenerateExponential(TimeSpan.FromMilliseconds(100), 3, factor));
        Assert.Equal("factor", ex.ParamName);
    }

    [Theory]
    [InlineData(-1.0)]
    public void GenerateLinear_NegativeFactor_ThrowsArgumentOutOfRangeException(double factor)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Retry.Quartz.GenerateLinear(TimeSpan.FromMilliseconds(100), 3, factor));
        Assert.Equal("factor", ex.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GenerateExponential_NonFiniteFactor_ThrowsArgumentOutOfRangeException(double factor)
    {
        // double.NaN не проходит проверку `factor < 1.0` (сравнение с NaN ложно), и каждая
        // задержка становилась NaN, что TimeSpan.FromMilliseconds отвергает уже при перечислении.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Retry.Quartz.GenerateExponential(TimeSpan.FromMilliseconds(100), 3, factor));
        Assert.Equal("factor", ex.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void GenerateLinear_NonFiniteFactor_ThrowsArgumentOutOfRangeException(double factor)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Retry.Quartz.GenerateLinear(TimeSpan.FromMilliseconds(100), 3, factor));
        Assert.Equal("factor", ex.ParamName);
    }

    [Fact]
    public void GenerateExponential_LargeRetryCount_SaturatesInsteadOfOverflowing()
    {
        // 100 мс * 2^46 ≈ 7·10^15 мс, что переполняет TimeSpan. Последовательность
        // материализуется целиком до первой попытки, поэтому 50 экспоненциальных повторов —
        // обычное значение — падало с OverflowException, не выполнив ни одной попытки.
        var delays = Retry.Quartz.GenerateExponential(TimeSpan.FromMilliseconds(100), 50, 2.0).ToArray();

        Assert.Equal(50, delays.Length);
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.MaxValue));
        Assert.Equal(TimeSpan.MaxValue, delays[^1]);
    }

    [Fact]
    public async Task GenerateExponential_PublicEntryPoint_DoesNotThrowBeforeTheFirstAttempt()
    {
        // Тот же сценарий через публичную точку входа: последовательность материализуется
        // до первой попытки, поэтому OverflowException приходило раньше, чем fun звали.
        // shouldRetry останавливает цикл на первой же ошибке — иначе тест реально ждал бы
        // насытившиеся задержки до TimeSpan.MaxValue.
        int attempts = 0;

        ValueTask<int> Throwing(int input, CancellationToken ct)
        {
            attempts++;
            return ValueTask.FromException<int>(new TransientException("always fails"));
        }

        var ex = await Assert.ThrowsAsync<TransientException>(() => Retry.Exponential(
            Throwing, 42, retryCount: 50, initialDelay: 100, factor: 2.0,
            shouldRetry: (_, _) => false,
            cancellationToken: TestContext.Current.CancellationToken).AsTask());

        // Насыщение вместо OverflowException: падает исходная ошибка, а не арифметика
        // генератора, и попытка была выполнена.
        Assert.Equal(1, attempts);
        Assert.Equal("always fails", ex.Message);
    }

    [Fact]
    public void GenerateLinear_HugeFactor_SaturatesInsteadOfOverflowing()
    {
        // factor = 1e308 делает increment бесконечным уже на второй итерации.
        var delays = Retry.Quartz.GenerateLinear(TimeSpan.FromMilliseconds(100), 5, 1e308).ToArray();

        Assert.Equal(5, delays.Length);
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.MaxValue));
    }

    #endregion

    #region Rethrow and critical exceptions

    [Fact]
    public async Task WaitAndRetry_AllAttemptsFailed_KeepsTheOriginalStackTrace()
    {
        // Arrange
        // Именно throw, а не ValueTask.FromException: трассировка снимается при броске,
        // поэтому кадр броска появляется в ней только если исключение действительно
        // бросается здесь.
        ValueTask<int> Throwing(int input, CancellationToken ct)
            => throw new InvalidOperationException("boom");

        // Act
        // `throw lastEx;` вне catch обнуляет трассировку: местом падения становился сам
        // Retry.WaitAndRetry, и вызывающий не видел, где именно упало.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Retry.Constant(Throwing, 42, retryCount: 1, waitTime: 1,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        // Assert
        Assert.NotNull(ex.StackTrace);
        Assert.Contains(nameof(Throwing), ex.StackTrace, StringComparison.Ordinal);
        Assert.Contains(nameof(Retry.WaitAndRetry), ex.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitAndRetry_NoInputOverload_AllAttemptsFailed_KeepsTheOriginalStackTrace()
    {
        // Arrange
        ValueTask<int> Throwing(CancellationToken ct)
            => throw new InvalidOperationException("boom");

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Retry.Constant(Throwing, retryCount: 1, waitTime: 1,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        // Assert
        Assert.NotNull(ex.StackTrace);
        Assert.Contains(nameof(Throwing), ex.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void IsFatal_AccessViolation_IsFatalAndCritical()
    {
        // Списки IsFatal и IsCritical расходились ровно на этом типе: повреждённое состояние
        // процесса было нерепитируемым для Retry и репитируемым для его вызывающих.
        Assert.True(Retry.IsFatal(new AccessViolationException()));
        Assert.True(new AccessViolationException().IsCritical());
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException), true)]
    [InlineData(typeof(StackOverflowException), true)]
    [InlineData(typeof(AppDomainUnloadedException), true)]
    [InlineData(typeof(BadImageFormatException), true)]
    [InlineData(typeof(CannotUnloadAppDomainException), true)]
    [InlineData(typeof(InvalidProgramException), true)]
    [InlineData(typeof(TransientException), false)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(TimeoutException), false)]
    public void IsFatal_AndIsCritical_NeverDisagree(Type exceptionType, bool expected)
    {
        // Arrange
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        // Assert
        Assert.Equal(expected, Retry.IsFatal(ex));
        Assert.Equal(expected, ex.IsCritical());
    }

    #endregion
}
