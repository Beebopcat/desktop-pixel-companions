using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace AzerothCompanion.Services;

internal static class CaptureNative
{
    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, uint length, out nint hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint hstring, in Guid iid, out nint factory);

    internal static IReadOnlyList<WindowChoice> ListWindows()
    {
        var windows = new List<WindowChoice>();
        var shell = GetShellWindow();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownPid) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(1024);
            GetWindowText(hwnd, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString())) return true;
            string name;
            try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
            catch { name = $"PID {pid}"; }
            windows.Add(new(hwnd, pid, title.ToString(), name));
            return true;
        }, 0);
        return windows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgi = 0, abi = 0;
        try
        {
            // BGRA support; WARP is a device fallback, never a capture-source fallback.
            int hr = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, out _, out context);
            if (hr < 0)
            {
                if (device != 0) { Marshal.Release(device); device = 0; }
                if (context != 0) { Marshal.Release(context); context = 0; }
                hr = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out device, out _, out context);
            }
            Marshal.ThrowExceptionForHR(hr);
            Guid dxgiId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in dxgiId, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out abi));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            if (abi != 0) Marshal.Release(abi);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
        }
    }

    internal static unsafe GraphicsCaptureItem CreateForWindow(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint hstring = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out hstring));
            Guid interopId = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in interopId, out factory));
            Guid itemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            Marshal.ThrowExceptionForHR(createForWindow(factory, hwnd, &itemId, &item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }
}
