namespace Sa.Data.TempFolder;

/// <summary>
/// Serialises cleanup deletion against the instance's file activity: <c>SaveStreamAsync</c> /
/// <c>CopyFileAsync</c> / <c>CreateSubfolder</c> and the debounced activity-marker walk enter
/// through <see cref="EnterActivityAsync"/> (shared — operations run side by side), while a
/// cleanup pass enters through <see cref="TryEnterCleanup"/> (exclusive) — and deletion is always
/// the side that gives way.
/// </summary>
/// <remarks>
/// Two rules make the exclusion complete:
/// <list type="bullet">
/// <item><b>A pass never starts on a busy instance.</b> While any activity is in flight,
/// <see cref="TryEnterCleanup"/> returns <see langword="null"/> — that pass is cancelled outright
/// (the caller reports 0 deleted) and the next interval simply retries.</item>
/// <item><b>An arriving activity cancels a running pass.</b> <see cref="EnterActivityAsync"/>
/// cancels the pass token — the deletion ends by cancellation at its next checkpoint instead of
/// racing the write — and waits only for the pass to unwind before proceeding.</item>
/// </list>
/// The debounced marker walk rides the activity side too, and its pending entry clears only once
/// the walk has landed — so a pass always sees either a refreshed marker (the folder is not
/// expired) or a pending entry (the folder is skipped): a file that was just written is never
/// deleted, even in the quiet window before its touch fires.
/// </remarks>
internal sealed class CleanupGate
{
    private readonly Lock _sync = new();
    private int _activity;
    private bool _passActive;
    private CancellationTokenSource? _interrupt;
    private TaskCompletionSource? _passExited;

    /// <summary>
    /// Enters the shared activity side: many operations run side by side, and a cleanup pass
    /// cannot overlap any of them. When a pass is active it is cancelled (deletion ends by
    /// cancellation) and this call waits for it to unwind, then enters.
    /// </summary>
    /// <param name="cancellationToken">Cancels the waiting — never the pass.</param>
    public async ValueTask<ActivityLease> EnterActivityAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            CancellationTokenSource? interrupt;
            Task exited;

            lock (_sync)
            {
                if (!_passActive)
                {
                    _activity++;
                    return new ActivityLease(this);
                }

                interrupt = _interrupt;
                exited = _passExited!.Task;
            }

            // The pass yields: it sees this cancellation at its next checkpoint and unwinds,
            // letting the write through instead of making it wait for a full pass.
            Cancel(interrupt);

            await exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tries to enter the exclusive cleanup side. Returns <see langword="null"/> when activity is
    /// in flight (or another pass is running) — that pass is cancelled, nothing waits.
    /// </summary>
    /// <param name="cancellationToken">The caller's token, linked into the pass token.</param>
    public CleanupLease? TryEnterCleanup(CancellationToken cancellationToken)
    {
        CancellationTokenSource interrupt;

        lock (_sync)
        {
            if (_passActive || _activity > 0)
            {
                return null;
            }

            _passActive = true;
            _passExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            interrupt = _interrupt = new CancellationTokenSource();
        }

        try
        {
            // One token for the whole pass: the caller's cancellation plus the interrupt an
            // arriving activity raises.
            return new CleanupLease(
                this, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interrupt.Token));
        }
        catch
        {
            // Never leave the gate wedged open if the lease could not be built.
            ReleasePass();
            throw;
        }
    }

    private void ExitActivity()
    {
        lock (_sync)
        {
            _activity--;
        }
    }

    private void ReleasePass()
    {
        TaskCompletionSource? exited;

        lock (_sync)
        {
            _passActive = false;
            _interrupt = null;
            exited = _passExited;
            _passExited = null;
        }

        // RunContinuationsAsynchronously: waiting activities resume on the pool, never inline
        // under the lock.
        exited?.TrySetResult();
    }

    private static void Cancel(CancellationTokenSource? interrupt)
    {
        if (interrupt is null)
        {
            return;
        }

        try
        {
            interrupt.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pass ended between the snapshot and the cancel — nothing to interrupt.
        }
        catch (AggregateException)
        {
            // A throwing callback must never hold up the write that raised the interrupt.
        }
    }

    /// <summary>Shared activity lease; disposing it (nesting allowed) releases one activity count.</summary>
    internal sealed class ActivityLease : IDisposable
    {
        private CleanupGate? _gate;

        internal ActivityLease(CleanupGate gate) => _gate = gate;

        public void Dispose()
        {
            var gate = _gate;
            if (gate is null)
            {
                return;
            }

            _gate = null;
            gate.ExitActivity();
        }
    }

    /// <summary>
    /// Exclusive cleanup lease: <see cref="Token"/> is cancelled when the caller's token is or
    /// when an arriving activity interrupts the pass.
    /// </summary>
    internal sealed class CleanupLease : IDisposable
    {
        private readonly CleanupGate _gate;
        private CancellationTokenSource? _source;

        internal CleanupLease(CleanupGate gate, CancellationTokenSource source)
        {
            _gate = gate;
            _source = source;
        }

        /// <summary>The pass token: caller cancellation and activity interrupts both land here.</summary>
        public CancellationToken Token => _source!.Token;

        public void Dispose()
        {
            var source = _source;
            if (source is null)
            {
                return;
            }

            _source = null;
            source.Dispose();
            _gate.ReleasePass();
        }
    }
}
