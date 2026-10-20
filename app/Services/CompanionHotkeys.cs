using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
namespace AzerothCompanion.Services;
public sealed class CompanionHotkeys : IDisposable
{
    private readonly IntPtr window; private readonly HwndSource? source;
    public event Action? ReadRequested,QuietRequested,TalkPressed,TalkReleased;
    private readonly DispatcherTimer talkRelease=new(){Interval=TimeSpan.FromMilliseconds(30)};
    public bool ReadRegistered {get;} public bool QuietRegistered {get;} public bool TalkRegistered {get;}
    public CompanionHotkeys(IntPtr hwnd)
    {
        window=hwnd;source=HwndSource.FromHwnd(hwnd);source?.AddHook(Hook);
        ReadRegistered=RegisterHotKey(hwnd,41010,0x4006,0x79);QuietRegistered=RegisterHotKey(hwnd,41012,0x4006,0x7B);TalkRegistered=RegisterHotKey(hwnd,41011,0x4006,0x7A);
        talkRelease.Tick+=(_,_)=>{if((GetAsyncKeyState(0x7A)&0x8000)==0){talkRelease.Stop();TalkReleased?.Invoke();}};
    }
    private IntPtr Hook(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled)
    {if(msg==0x312){if(w.ToInt32()==41010)ReadRequested?.Invoke();if(w.ToInt32()==41012)QuietRequested?.Invoke();if(w.ToInt32()==41011&&!talkRelease.IsEnabled){TalkPressed?.Invoke();talkRelease.Start();}
        // Claim only this class's own hotkeys; other features (Quality of Life) register theirs on the same window.
        if(w.ToInt32() is 41010 or 41011 or 41012)handled=true;}return IntPtr.Zero;}
    public void Dispose(){talkRelease.Stop();if(ReadRegistered)UnregisterHotKey(window,41010);if(QuietRegistered)UnregisterHotKey(window,41012);if(TalkRegistered)UnregisterHotKey(window,41011);source?.RemoveHook(Hook);}
    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]static extern bool UnregisterHotKey(IntPtr h,int id);
    [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
}
