using System.Runtime.InteropServices;

namespace AzerothCompanion.Services;

public readonly record struct MonitorPixelBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(MonitorPixelBounds other) => other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;
}

public sealed record PrimaryMonitorLayout(nint Handle, string Device, MonitorPixelBounds Monitor, MonitorPixelBounds WorkArea);

/// <summary>Uses MONITORINFOF_PRIMARY, never a display number or selected game's monitor.</summary>
public static class MonitorDesktopService
{
    public static PrimaryMonitorLayout GetPrimary()
    {
        PrimaryMonitorLayout? primary = null;
        MonitorCallback callback = (nint handle, nint dc, ref NativeRect bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(handle, ref info) && (info.Flags & 1) != 0)
                primary = new(handle, info.Device, Convert(info.Monitor), Convert(info.Work));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0) || primary == null)
            throw new InvalidOperationException("Windows did not report a primary monitor; desktop companions cannot be placed safely.");
        return primary;
    }

    public static MonitorPixelBounds GetWindowBounds(nint handle)
    {
        if (!GetWindowRect(handle, out var bounds)) return default;
        return Convert(bounds);
    }

    public static bool IsWindowOnPrimary(nint handle)
    {
        if (handle == 0 || !IsWindow(handle) || IsIconic(handle)) return false;
        try
        {
            var primary = GetPrimary(); var bounds = GetWindowBounds(handle);
            if (DwmGetWindowAttribute(handle, 9, out NativeRect visible, Marshal.SizeOf<NativeRect>()) == 0 && visible.Right > visible.Left && visible.Bottom > visible.Top)
                bounds = Convert(visible);
            // The complete selected window must remain on the primary display. A
            // straddling window could expose a secondary screen during capture.
            return bounds.Width > 0 && bounds.Height > 0 && primary.Monitor.Contains(bounds);
        }
        catch (InvalidOperationException) { return false; }
    }

    private static MonitorPixelBounds Convert(NativeRect r) => new(r.Left, r.Top, r.Right, r.Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out NativeRect value, int size);
}
