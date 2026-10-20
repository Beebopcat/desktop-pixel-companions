namespace AzerothCompanion.Services;

/// <summary>A sampled result belongs to one generation and one revocable target permission.</summary>
internal readonly record struct CaptureLease(long Generation, WindowCaptureGuard Guard)
{
    internal bool Permits(long generation, WindowCaptureGuard? currentGuard, bool capturing) =>
        capturing && generation == Generation && ReferenceEquals(Guard, currentGuard) && Guard.CheckNow();

    // Caller holds its state lock so checking ownership and returning a cached value share one boundary.
    internal T? Publish<T>(T? value, long generation, WindowCaptureGuard? currentGuard, bool capturing) where T : class =>
        Permits(generation, currentGuard, capturing) ? value : null;
}

/// <summary>Independent bounded watchdog plus synchronous frame gate. A revoked lease never reopens itself.</summary>
internal sealed class WindowCaptureGuard : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly Func<bool> allowed;
    private readonly Action revoked;
    private readonly Timer? timer;
    private int state; // 0 active, 1 revoked, 2 disposed
    internal WindowCaptureGuard(Func<bool> allowed, Action revoked, bool startTimer = true)
        : this(allowed, _ => revoked(), startTimer) { }
    internal WindowCaptureGuard(Func<bool> allowed, Action<WindowCaptureGuard> revoked, bool startTimer = true)
    {
        this.allowed = allowed; this.revoked = () => revoked(this);
        if (startTimer) timer = new(_ => CheckNow(), null, PollInterval, PollInterval);
    }
    internal bool CheckNow()
    {
        if (Volatile.Read(ref state) != 0) return false;
        bool permit;
        try { permit = allowed(); } catch { permit = false; }
        if (!permit && Interlocked.CompareExchange(ref state, 1, 0) == 0)
        {
            try { timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            revoked();
        }
        return permit && Volatile.Read(ref state) == 0;
    }
    public void Dispose() { Interlocked.Exchange(ref state, 2); timer?.Dispose(); }
}
