using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzerothCompanion.Services;

internal static class GameTextScope
{
    internal static bool IsCurrent(WindowChoice target)
    {
        if(target.Handle==0||target.ProcessId==0||!IsWindow(target.Handle)||!IsWindowVisible(target.Handle)||IsIconic(target.Handle))return false;
        GetWindowThreadProcessId(target.Handle,out var pid);if(pid!=target.ProcessId)return false;
        try{using var process=Process.GetProcessById((int)pid);return process.ProcessName.Equals(target.ProcessName,StringComparison.OrdinalIgnoreCase);}
        catch{return false;}
    }
    [DllImport("user32.dll")]private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
}
