using System.Diagnostics;

namespace Sa.Classes;

/// <summary>
/// Фоновое продление блокировки по таймеру.
/// <para>
/// Если <c>extendLocked</c> бросает исключение, продление <b>прекращается навсегда</b>, а
/// блокировка истекает — то есть её начинает ждать кто-то другой, пока текущий владелец
/// считает, что работает под замком. Поэтому ошибка не глотается: она копится и
/// выбрасывается из <see cref="LockRenewerHandle.DisposeAsync"/>. Раньше здесь стоял
/// <c>Debug.WriteLine</c>, который в Release вырезается целиком: цикл тихо умирал после
/// первого же сбоя, и про это не узнавал никто.
/// </para>
/// </summary>
internal static class LockRenewer
{
    public static LockRenewerHandle KeepLocked(
        TimeSpan lockExpiration,
        Func<CancellationToken, Task> extendLocked,
        bool blockImmediately = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockExpiration, TimeSpan.Zero, nameof(lockExpiration));

        return new LockRenewerHandle(lockExpiration, extendLocked, blockImmediately, cancellationToken);
    }

    public static async Task<bool> WaitForConditionAsync(
        Func<CancellationToken, Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        var sw = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (sw.Elapsed < timeout)
            {
                if (!cancellationToken.IsCancellationRequested
                    && await predicate(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            /* ignore */
        }

        return false;
    }
}

/// <summary>
/// Живой держатель блокировки. Освобождает таймер и, если продление упало, сообщает об этом
/// при освобождении.
/// </summary>
internal sealed class LockRenewerHandle : IDisposable, IAsyncDisposable
{
    private readonly PeriodicTimer _timer;

    /// <summary>
    /// Гасит цикл продления. Свой, а не токен вызывающего: освобождение держателя обязано
    /// останавливать фоновое продление само по себе, иначе вызывающий, который отменил
    /// токен где-то ещё, держал бы блокировку живой.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Живёт столько же, сколько цикл: освобождать раньше нельзя, на отменённом
    /// <see cref="CancellationTokenSource"/> токен продолжает «отменён», но регистрация
    /// новых обработчиков бросает <see cref="ObjectDisposedException"/>.
    /// </summary>
    private readonly CancellationTokenSource _linkedCts;

    private readonly Task _task;

    /// <summary>
    /// Первая ошибка продления, если оно упало. <c>null</c>, пока блокировка продлевается
    /// или отменена штатно.
    /// </summary>
    public Exception? RenewalError { get; private set; }

    public LockRenewerHandle(
        TimeSpan lockExpiration,
        Func<CancellationToken, Task> extendLocked,
        bool blockImmediately,
        CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(lockExpiration);
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);

        // CancellationToken.None, а не cancellationToken: с уже отменённым токеном Task.Run
        // создал бы Canceled-задачу, и extendLocked при blockImmediately не выполнился бы
        // ни разу — тихий пропуск первого продления.
        _task = Task.Run(() => RenewLoopAsync(extendLocked, blockImmediately, _linkedCts.Token), CancellationToken.None);
    }

    private async Task RenewLoopAsync(
        Func<CancellationToken, Task> extendLocked,
        bool blockImmediately,
        CancellationToken cancellationToken)
    {
        try
        {
            if (blockImmediately)
            {
                await extendLocked(cancellationToken).ConfigureAwait(false);
            }

            while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await extendLocked(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Штатная отмена — не ошибка.
        }
        catch (Exception e)
        {
            RenewalError = e;
            Debug.WriteLine(e);
        }
    }

    public void Dispose()
    {
        // Гасим цикл, а не только таймер: иначе фоновое продление продолжалось бы до
        // следующего тика после освобождения держателя. CancelAsync, а не Cancel, чтобы
        // Dispose не блокировался на продолжениях внутри extendLocked.
        _stopping.CancelAsync();
        _timer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _timer.Dispose();
        await _task.ConfigureAwait(false);

        if (RenewalError != null)
        {
            throw new LockRenewalException(
                "Lock renewal has failed and the lock was no longer being extended.", RenewalError);
        }
    }
}

/// <summary>
/// Продление блокировки оборвалось ошибкой: блокировка истекла, её может забрать другой владелец.
/// </summary>
internal sealed class LockRenewalException(string message, Exception inner) : Exception(message, inner);
