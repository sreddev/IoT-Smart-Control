namespace LedvanceControl.Core.Services;

/// <summary>
/// Sends changes without waiting: the first one runs at once; while it is in flight (and for at least
/// <c>interval</c> after it started) newer changes replace each other and only the latest runs next.
/// A click reacts immediately, a slider drag streams at most one command per interval, and the final
/// position is always sent.
/// </summary>
public sealed class LatestOnlySender(TimeSpan interval, Action<Exception>? onError = null)
{
    private readonly object _gate = new();
    private Func<Task>? _next;
    private bool _busy;
    private Task _pump = Task.CompletedTask;

    public void Run(Func<Task> action)
    {
        lock (_gate)
        {
            _next = action;
            if (_busy) return;
            _busy = true;
            _pump = Task.Run(PumpAsync);
        }
    }

    /// <summary>
    /// True while changes are still being sent. A control should not reload itself from the device
    /// meanwhile: the device reports values the user has already moved past.
    /// </summary>
    public bool IsBusy
    {
        get { lock (_gate) return _busy; }
    }

    /// <summary>Completes once everything queued so far has been sent (for tests).</summary>
    public Task IdleAsync()
    {
        lock (_gate) return _pump;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Func<Task>? action;
            lock (_gate)
            {
                action = _next;
                _next = null;
                if (action == null) { _busy = false; return; }
            }
            var started = Environment.TickCount64;
            try { await action(); }
            catch (Exception ex) { onError?.Invoke(ex); }
            var rest = interval.TotalMilliseconds - (Environment.TickCount64 - started);
            if (rest > 0) await Task.Delay(TimeSpan.FromMilliseconds(rest));
        }
    }
}
