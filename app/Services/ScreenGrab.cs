using System.Runtime.InteropServices;

namespace AzerothCompanion.Services;

/// <summary>One physical display. <see cref="Number"/> is its position in Windows' device-name order, so Display 1 and Display 2 are stable.</summary>
public sealed record ScreenDisplay(int Number, string Device, MonitorPixelBounds Bounds, bool Primary)
{
    /// <summary>Number 0 is the "active window" entry: whichever other app window you used last.</summary>
    public bool IsActiveWindow => Number == 0;
    public static ScreenDisplay ActiveWindowEntry { get; } = new(0, "active-window", default, false);
    public string Label => IsActiveWindow ? "Active window (the one you were just using)"
        : $"Display {Number} · {Bounds.Width}×{Bounds.Height}{(Primary ? " · main display" : "")}";
    public override string ToString() => Label;
}

/// <summary>User-started, whole-display snapshots for reading text from any app (browser, document, game). Never runs unless a read is requested.</summary>
public static class ScreenGrab
{
    public static IReadOnlyList<ScreenDisplay> ListDisplays()
    {
        var found = new List<(string Device, MonitorPixelBounds Bounds, bool Primary)>();
        MonitorCallback callback = (nint handle, nint dc, ref NativeRect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(handle, ref info))
                found.Add((info.Device, new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom), (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0) || found.Count == 0)
            throw new InvalidOperationException("Windows did not report any displays.");
        return found.OrderBy(d => DeviceNumber(d.Device)).ThenBy(d => d.Bounds.Left)
            .Select((d, i) => new ScreenDisplay(i + 1, d.Device, d.Bounds, d.Primary)).ToArray();
    }

    private static int DeviceNumber(string device)
    {
        var digits = new string(device.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    public static CapturedFrame Capture(ScreenDisplay display) => CaptureRect(display.Bounds, display.Label);

    public static CapturedFrame CaptureRect(MonitorPixelBounds bounds, string label)
    {
        var display = new ScreenDisplay(-1, label, bounds, false);
        int width = display.Bounds.Width, height = display.Bounds.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("That display reports no size.");
        nint screen = GetDC(0);
        if (screen == 0) throw new InvalidOperationException("Windows would not give access to the screen.");
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref header, 0, out nint bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new InvalidOperationException("Could not allocate the screen image.");
            previous = SelectObject(memory, bitmap);
            // SRCCOPY | CAPTUREBLT includes layered windows such as browsers using hardware acceleration.
            if (!BitBlt(memory, 0, 0, width, height, screen, display.Bounds.Left, display.Bounds.Top, 0x00CC0020 | 0x40000000))
                throw new InvalidOperationException("Windows refused to copy that display.");
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new CapturedFrame(width, height, pixels, DateTimeOffset.UtcNow, display.Label)
            { OriginX = display.Bounds.Left, OriginY = display.Bounds.Top, SourceWidth = width, SourceHeight = height };
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Blacks out this app's own visible windows (main window, companions, grass) in a captured frame, so it never reads itself.
    /// Pure pixel editing - no window flags are touched, so the desktop compositor is not disturbed.</summary>
    public static void MaskOwnWindows(CapturedFrame frame)
    {
        uint own = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != own || !IsWindowVisibleNative(window) || IsIconicNative(window)) return true;
            NativeRect r;
            if (DwmGetWindowAttributeNative(window, 9, out r, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRectNative(window, out r)) return true;
            int left = Math.Max(r.Left, frame.OriginX) - frame.OriginX, top = Math.Max(r.Top, frame.OriginY) - frame.OriginY;
            int right = Math.Min(r.Right, frame.OriginX + frame.Width) - frame.OriginX, bottom = Math.Min(r.Bottom, frame.OriginY + frame.Height) - frame.OriginY;
            if (right - left < 4 || bottom - top < 4) return true;
            for (int y = top; y < bottom; y++)
            {
                int i = (y * frame.Width + left) * 4;
                for (int x = left; x < right; x++, i += 4) { frame.BgraPixels[i] = 0; frame.BgraPixels[i + 1] = 0; frame.BgraPixels[i + 2] = 0; frame.BgraPixels[i + 3] = 255; }
            }
            return true;
        }, 0);
    }
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsWindowVisibleNative(nint window);
    [DllImport("user32.dll", EntryPoint = "IsIconic")] private static extern bool IsIconicNative(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool GetWindowRectNative(nint window, out NativeRect rect);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeNative(nint window, uint attribute, out NativeRect value, int size);

    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>Hides every window of this app (main, companions, grass) from screen captures, or restores them. Used only while reading.</summary>
    public static void ExcludeOwnWindows(bool exclude)
    {
        uint own = (uint)Environment.ProcessId;
        EnumWindows((window, _) => { GetWindowThreadProcessId(window, out uint pid); if (pid == own) ExcludeFromCapture(window, exclude); return true; }, 0);
    }

    /// <summary>Keeps this app's own windows out of screen captures, so it never reads its own interface back.</summary>
    public static void ExcludeFromCapture(nint hwnd, bool exclude)
    {
        if (hwnd == 0) return;
        try { SetWindowDisplayAffinity(hwnd, exclude ? 0x11u : 0u); } catch { }
    }

    /// <summary>Remembers the last window you used that is not this app, so "Read" and the hotkey know what you mean.</summary>
    public static class ActiveWindow
    {
        public static nint LastOther { get; private set; }
        /// <summary>Test hook: target one specific window instead of the tracked one.</summary>
        public static void SetForTest(nint hwnd) => LastOther = hwnd;
        private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
            { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland" };
        public static void Poll(uint ownProcessId)
        {
            var h = GetForegroundWindow();
            if (h == 0 || !IsWindowVisible(h) || IsIconic(h)) return;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == ownProcessId) return;
            var name = new System.Text.StringBuilder(64); GetClassName(h, name, 64);
            if (Shell.Contains(name.ToString())) return;
            LastOther = h;
        }
        public static bool TryDescribe(out MonitorPixelBounds bounds, out string title)
        {
            bounds = default; title = "";
            var h = LastOther;
            if (h == 0 || !IsWindow(h) || IsIconic(h)) return false;
            if (DwmGetWindowAttribute(h, 9, out NativeRect r, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(h, out r)) return false;
            if (r.Right - r.Left < 40 || r.Bottom - r.Top < 40) return false;
            var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); title = sb.ToString();
            bounds = new(r.Left, r.Top, r.Right, r.Bottom); return true;
        }
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
        [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out NativeRect r);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint h, uint attribute, out NativeRect value, int size);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
