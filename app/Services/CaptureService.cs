using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace AzerothCompanion.Services;
public sealed record CaptureNotification(long Generation,string Message);

/// <summary>Explicit window-only Windows Graphics Capture. No screen, input, or process-memory APIs.</summary>
public sealed class CaptureService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private IReadOnlyList<WindowChoice> _listedWindows = Array.Empty<WindowChoice>();
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFramePool? _pool;
    private Direct3D11CaptureFrame? _latestFrame;
    private CapturedFrame? _lastImage;
    private DateTimeOffset _latestAt;
    private TaskCompletionSource<bool> _available = NewSignal();
    private SizeInt32 _size;
    private WindowChoice? _window;
    private WindowCaptureGuard? _targetGuard;
    private long _generation;
    private bool _disposed;
    private bool _resizePending;
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCapturing { get { lock (_gate) return _session != null; } }
    public string? WindowTitle { get { lock (_gate) return _window?.Title; } }
    public long Generation { get { lock (_gate) return _generation; } }
    public event EventHandler<CaptureNotification>? TargetClosed;
    public event EventHandler<CaptureNotification>? CaptureFailed;
    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public IReadOnlyList<WindowChoice> ListWindows()
    {
        var windows = CaptureNative.ListWindows();
        lock (_gate) _listedWindows = windows;
        return windows;
    }

    public Task<bool> StartWindowAsync(nint hwnd)
    {
        WindowChoice? choice;
        lock (_gate) choice = _listedWindows.FirstOrDefault(w => w.Handle == hwnd);
        if (choice == null) throw new InvalidOperationException("Refresh the window list and select a window before starting.");
        return StartWindowAsync(choice);
    }

    public Task<bool> StartWindowAsync(WindowChoice choice) => StartWindowCore(choice, null);

    private Task<bool> StartWindowCore(WindowChoice choice, CaptureLease? refreshLease)
    {
        lock (_lifecycle)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A resize retains the original revocable permission. It is never an implicit new Start.
        if (refreshLease is { } expected && !TargetPermit(expected)) return Task.FromResult(false);
        if (!IsSupported) throw new NotSupportedException("Windows Graphics Capture is unavailable on this computer.");
        if (!CaptureNative.IsWindow(choice.Handle)) throw new InvalidOperationException("The selected window has closed. Refresh the window list.");
        CaptureNative.GetWindowThreadProcessId(choice.Handle, out uint pid);
        if (pid != choice.ProcessId) throw new InvalidOperationException("The selected window changed. Refresh the window list before starting.");
        if (CaptureNative.IsIconic(choice.Handle)) throw new InvalidOperationException("Restore the selected window before starting capture.");
        if (!TargetAllowed(choice)) throw new InvalidOperationException("The selected window must stay visible and entirely on the primary display.");
        var preservedGuard = refreshLease?.Guard;
        StopCore(preservedGuard);
        try
        {
            lock (_gate)
            {
                if (preservedGuard != null && !preservedGuard.CheckNow()) return Task.FromResult(false);
                _device = CaptureNative.CreateDevice();
                _item = CaptureNative.CreateForWindow(choice.Handle);
                _size = _item.Size;
                if (_size.Width <= 0 || _size.Height <= 0) throw new InvalidOperationException("The selected window has no capturable surface.");
                _window = choice; _available = NewSignal(); _resizePending = false;
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _size);
                _pool.FrameArrived += OnFrameArrived;
                _item.Closed += OnItemClosed;
                _session = _pool.CreateCaptureSession(_item);
                if (preservedGuard == null)
                    _targetGuard = new(() => TargetAllowed(choice), QueueTargetRejection);
                _session.IsCursorCaptureEnabled = false;
                // The operating-system capture border remains enabled.
                if (!_targetGuard!.CheckNow()) return Task.FromResult(false);
                _session.StartCapture();
            }
        }
        catch { StopCore(preservedGuard); throw; }
        return Task.FromResult(true);
        }
    }

    /// <summary>Latest selected-window frame, with its actual capture timestamp. Static windows may reuse unchanged pixels.</summary>
    public async Task<CapturedFrame?> CaptureFrameAsync(NormalizedRegion? region = null, CancellationToken ct = default)
    {
        region?.Validate();
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        Direct3D11CaptureFrame? frame = null;
        CaptureLease lease;
        try
        {
            // A static window may not submit another surface after pool recreation. Restart
            // the same selected window on demand, so resizing cannot leave sampling stalled.
            lock (_lifecycle)
            {
                WindowChoice? resizeWindow;
                lock (_gate)
                {
                    if (_targetGuard == null || _session == null) return null;
                    lease = new(_generation, _targetGuard);
                    resizeWindow = _resizePending ? _window : null;
                }
                if (!TargetPermit(lease)) return null;
                if (resizeWindow != null)
                {
                    if (!StartWindowCore(resizeWindow, lease).GetAwaiter().GetResult()) return null;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_targetGuard, lease.Guard)) return null;
                        lease = new(_generation, lease.Guard);
                    }
                }
            }
            if (!TargetPermit(lease)) return null;
            Task<bool>? wait;
            lock (_gate)
            {
                if (!LeaseCurrent(lease)) return null;
                wait = _latestFrame == null && _lastImage == null ? _available.Task : null;
            }
            if (wait != null)
            {
                try { if (!await wait.WaitAsync(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false)) return null; }
                catch (TimeoutException) { return null; }
            }
            if (!TargetPermit(lease)) return null;
            string title;
            DateTimeOffset at;
            CapturedFrame? result;
            lock (_gate)
            {
                if (!LeaseCurrent(lease)) return null;
                frame = _latestFrame; _latestFrame = null;
                _available = NewSignal();
                result = _lastImage;
                title = _window?.Title ?? "Selected window"; at = _latestAt;
            }
            if (frame != null)
            {
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore).AsTask(ct).ConfigureAwait(false);
                int width = Math.Min(frame.ContentSize.Width, bitmap.PixelWidth), height = Math.Min(frame.ContentSize.Height, bitmap.PixelHeight);
                var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
                bitmap.CopyToBuffer(pixels.AsBuffer());
                result = new(bitmap.PixelWidth, bitmap.PixelHeight, pixels, at, title) { SourceWidth = width, SourceHeight = height };
                if (width != bitmap.PixelWidth || height != bitmap.PixelHeight)
                    result = result.Crop(new(0, 0, (double)width / bitmap.PixelWidth, (double)height / bitmap.PixelHeight));
                lock (_gate) { if (!LeaseCurrent(lease)) return null; _lastImage = result; }
            }
            // Crop can be nontrivial: check the original generation/guard only after it completes.
            var publication = result == null || region == null ? result : result.Crop(region.Value);
            lock (_gate) return lease.Publish(publication, _generation, _targetGuard, _session != null);
        }
        finally { frame?.Dispose(); _requests.Release(); }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        Direct3D11CaptureFrame? old = null;
        try
        {
            if (!TargetPermit()) return;
            bool resize;
            SizeInt32 size;
            lock (_gate)
            {
                if (!ReferenceEquals(sender, _pool)) return;
                frame = sender.TryGetNextFrame();
                if (frame == null) return;
                size = frame.ContentSize;
                if (size.Width <= 0 || size.Height <= 0) return;
                if (!TargetPermit()) return;
                old = _latestFrame; _latestFrame = null;
                resize = size.Width != _size.Width || size.Height != _size.Height;
                if (resize) { _resizePending = true; _lastImage = null; }
                else { _latestFrame = frame; frame = null; _latestAt = DateTimeOffset.UtcNow; _available.TrySetResult(true); }
            }
            old?.Dispose(); old = null;
            if (resize)
            {
                frame?.Dispose(); frame = null;
                // Refresh is deferred to the next explicit sampling call; no continuous CPU copies.
            }
        }
        catch (Exception ex)
        {
            bool current;long notificationGeneration;
            lock (_gate) { current = ReferenceEquals(sender, _pool);notificationGeneration=_generation; if (current) _available.TrySetException(ex); }
            if (current) CaptureFailed?.Invoke(this, new(notificationGeneration,$"Selected-window capture failed: {ex.Message}"));
        }
        finally { frame?.Dispose(); old?.Dispose(); }
    }

    private static bool TargetAllowed(WindowChoice choice)
    {
        if (!MonitorDesktopService.IsWindowOnPrimary(choice.Handle)) return false;
        CaptureNative.GetWindowThreadProcessId(choice.Handle, out uint pid);
        return pid == choice.ProcessId;
    }
    private bool TargetPermit()
    {
        WindowCaptureGuard? guard;
        lock (_gate) guard = _targetGuard;
        return guard?.CheckNow() == true;
    }
    private bool TargetPermit(CaptureLease lease) { lock (_gate) return LeaseCurrent(lease); }
    // Call under _gate, including at the final publication boundary.
    private bool LeaseCurrent(CaptureLease lease) => lease.Permits(_generation, _targetGuard, _session != null);

    private void QueueTargetRejection(WindowCaptureGuard guard)
    {
        // Guard revocation rejects frames immediately. Native disposal is off the event callback thread.
        _ = Task.Run(() =>
        {
            long notice;
            lock (_lifecycle)
            {
                lock (_gate) { if (!ReferenceEquals(guard, _targetGuard)) return; _available.TrySetResult(false); }
                Stop(); notice = Generation;
            }
            CaptureFailed?.Invoke(this, new(notice, "Capture stopped: the selected window minimized, closed, changed process or left the primary display. Start explicitly to resume."));
        });
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        lock (_gate) { if (!ReferenceEquals(sender, _item)) return; }
        // Do not dispose an event source on its own callback thread.
        _ = Task.Run(() =>
        {
            long notificationGeneration;
            lock (_lifecycle)
            {
                lock (_gate) { if (!ReferenceEquals(sender, _item)) return; }
                Stop();
                lock(_gate)notificationGeneration=_generation;
            }
            TargetClosed?.Invoke(this, new(notificationGeneration,"The selected window closed. Capture and speech stopped."));
        });
    }

    public void Stop() => StopCore(null);

    private void StopCore(WindowCaptureGuard? preserveGuard)
    {
        lock (_lifecycle)
        {
        GraphicsCaptureItem? item;
        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? pool;
        Direct3D11CaptureFrame? frame;
        IDirect3DDevice? device;
        WindowCaptureGuard? guard;
        lock (_gate)
        {
            ++_generation; _available.TrySetResult(false);
            item = _item; _item = null; session = _session; _session = null;
            pool = _pool; _pool = null; device = _device; _device = null;
            frame = _latestFrame; _latestFrame = null; _lastImage = null; _window = null; _resizePending = false;
            guard = _targetGuard; _targetGuard = preserveGuard;
        }
        if (!ReferenceEquals(guard, preserveGuard)) guard?.Dispose();
        // Event unsubscription and native disposal can wait for callbacks; never hold the state lock here.
        if (item != null) item.Closed -= OnItemClosed;
        if (pool != null) pool.FrameArrived -= OnFrameArrived;
        frame?.Dispose(); session?.Dispose(); pool?.Dispose(); device?.Dispose();
        }
    }

    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }
}
