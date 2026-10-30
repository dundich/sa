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

    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns <see langword="true"/>,
    /// <paramref name="timeout"/> elapses, or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only if the predicate was satisfied. A timeout and a cancellation
    /// both report <see langword="false"/> — cancellation is not an error here.
    /// </returns>
    /// <remarks>
    /// The wait between polls is bounded by the time left, and the deadline is checked before each
    /// poll. The loop used to sleep a full <paramref name="pollInterval"/> and only then re-check
    /// <c>sw.Elapsed &lt; timeout</c>, so a 1 s poll with a 100 ms timeout still took a second and
    /// ran the predicate past the deadline. It also built a <see cref="PeriodicTimer"/> for what
    /// is a single sleep, which rejected a non-positive interval with an
    /// <see cref="ArgumentOutOfRangeException"/> of its own instead of the documented one.
    /// </remarks>
    public static async Task<bool> WaitForConditionAsync(
        Func<CancellationToken, Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        TimeSpan interval = pollInterval ?? DefaultPollInterval;
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), interval, "Poll interval must be > 0.");
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be ≥ 0.");

        var sw = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                if (sw.Elapsed >= timeout)
                    return false;

                if (!cancellationToken.IsCancellationRequested
                    && await predicate(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                // Не хватает времени ещё на один полный цикл опроса. Не стоит ни делать ещё один
                // вызов предиката, ни ждать остаток в теле цикла: Task.Delay(remaining) с крошечным
                // remaining мог бы сработать раньше, чем sw пересеёт timeout, и цикл разогнал бы
                // tight-loop (уже было 4885 вызовов за один прогон). Поэтому остаток дожидается
                // отдельным циклом: он не делает новых вызовов предиката, а выходит, как только
                // sw переступает boundary. Task.Delay ждёт не меньше заданного, так что
                // несколько коротких ожиданий здесь сходятся за пару итераций, а не в бесконечность.
                TimeSpan remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return false;

                if (remaining < interval)
                {
                    while (sw.Elapsed < timeout)
                    {
                        await Task.Delay(timeout - sw.Elapsed, cancellationToken).ConfigureAwait(false);
                    }

                    return false;
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            /* ignore */
        }

        return false;
    }

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(10);
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
    /// Признак того, что освобождение уже прошло через <see cref="DisposeAsync"/>. Синхронный
    /// <see cref="Dispose"/> не дожидается цикла, поэтому освобождать источники отмены там
    /// нельзя — иначе они достались бы ещё работающему циклу. Флаг нужен, чтобы второе
    /// освобождение не дёргало уже освобождённый источник.
    /// </summary>
    private int _asyncDisposed;

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
        catch (OperationCanceledException oce)
        {
            // Токен не отменён, а extendLocked вернул OCE: отмена на стороне команды (Npgsql
            // бросает его из Command.Cancel, например при снятии блокировки строки), а не
            // наш таймаут. Продление всё равно не состоялось, то есть блокировка потеряна, —
            // но по другой причине, и в логе это должно читаться иначе.
            RenewalError = new LockRenewalException(
                "Lock renewal was cancelled by the extension call itself rather than by shutdown.",
                oce);
            Debug.WriteLine(RenewalError);
        }
        catch (Exception e)
        {
            RenewalError = e;
            Debug.WriteLine(e);
        }
    }

    public void Dispose()
    {
        // Асинхронное освобождение уже погасило цикл и освободило CTS — второй раз трогать их
        // нельзя, Cancel на освобождённом источнике бросает ObjectDisposedException.
        if (Volatile.Read(ref _asyncDisposed) == 1) return;

        // Гасим цикл, а не только таймер: иначе фоновое продление продолжалось бы до
        // следующего тика после освобождения держателя. CancelAsync, а не Cancel, чтобы
        // Dispose не блокировался на продолжениях внутри extendLocked.
        _stopping.CancelAsync();
        _timer.Dispose();

        // CancellationTokenSource здесь освобождать нельзя: цикл ещё жив и продолжает
        // пользоваться его токеном, а Dispose не дожидается его завершения. Освобождается
        // в DisposeAsync, который дожидается.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _asyncDisposed, 1) == 0)
        {
            _stopping.Cancel();
            _timer.Dispose();
            await _task.ConfigureAwait(false);

            // Цикл гарантированно завершён, значит токен больше никем не используется, а
            // связанный CTS держит регистрацию на токене вызывающего — её надо снять.
            _linkedCts.Dispose();
            _stopping.Dispose();
        }

        // Повторное освобождение тоже сообщает об ошибке: вызывающий мог не обработать её
        // в первый раз, и молчаливое «уже освобождено» сделало бы потерю блокировки незаметной.
        if (RenewalError != null)
        {
            // Не двойная обёртка: ветка отмены на стороне команды уже сформулировала
            // LockRenewalException с точным объяснением, заворачивать его в ещё один
            // значило бы спрятать эту формулировку за общим текстом.
            throw RenewalError as LockRenewalException
                ?? new LockRenewalException(
                    "Lock renewal has failed and the lock was no longer being extended.", RenewalError);
        }
    }
}

/// <summary>
/// Продление блокировки оборвалось ошибкой: блокировка истекла, её может забрать другой владелец.
/// </summary>
internal sealed class LockRenewalException(string message, Exception inner) : Exception(message, inner);
