namespace AzerothCompanion.Services;

/// <summary>Automatic refresh skips occupied work; an explicit question can await its cancellation/drain.</summary>
internal sealed class ResearchWorkGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<IDisposable?> EnterAsync(bool waitForCurrent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (waitForCurrent) await gate.WaitAsync(ct).ConfigureAwait(false);
        else if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        if (ct.IsCancellationRequested) { gate.Release(); ct.ThrowIfCancellationRequested(); }
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); }
    }
}
