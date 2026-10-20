namespace AzerothCompanion.Services;

internal readonly record struct SelectedWindowKey(uint ProcessId, nint Handle);

/// <summary>Pure ownership checks shared by selected capture startup and held-start fixtures.</summary>
internal sealed class CaptureStartOwnership(long epoch, CancellationToken token, Func<long> currentEpoch,
    Func<SelectedWindowKey?> selectedWindow, SelectedWindowKey? expectedSelection)
{
    internal bool IsCurrent() => !token.IsCancellationRequested && currentEpoch() == epoch &&
        (expectedSelection is null || selectedWindow() == expectedSelection);

    // The caller holds captureStartGate until this operation returns. An old
    // startup therefore stops its own capture before a newer startup can enter.
    internal async Task<bool> StartCaptureAsync(Func<Task> start, Action stop, Func<bool> additionalChecks)
    {
        bool Current() => IsCurrent() && additionalChecks();
        if (!Current()) return false;
        await start();
        if (Current()) return true;
        stop();
        return false;
    }
}
