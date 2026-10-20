using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzerothCompanion.Services;

// UI Automation providers and delayed clipboard renderers can block indefinitely. Each explicit
// read owns a killable, hidden MTA worker; no provider call ever runs on the application UI thread.
internal static class AccessibleTextWorker
{
    internal sealed record Request(string Mode,long Handle,uint ProcessId,string ProcessName,
        uint ProducerProcessId=0,string ProducerProcessName="",uint? PreviousSequence=null);
    internal sealed record Result(string Status,string Message,string[]? Blocks=null,uint Sequence=0);
    internal static Task<Result> ReadAsync(Request request,TimeSpan timeout,CancellationToken ct)=>
        Task.Run(()=>RunAsync(request,timeout,ct,Script,null),CancellationToken.None);
    internal static Task<Result> ReadFixtureAsync(string script,TimeSpan timeout,CancellationToken ct,Action<int> started)=>
        Task.Run(()=>RunAsync(new("Fixture",0,0,""),timeout,ct,script,started),CancellationToken.None);
    private static async Task<Result> RunAsync(Request request,TimeSpan timeout,CancellationToken ct,string script,Action<int>? started)
    {
        ct.ThrowIfCancellationRequested();
        var executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var argument in new[]{"-NoLogo","-NoProfile","-NonInteractive","-Mta","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})info.ArgumentList.Add(argument);
        using var process=new Process{StartInfo=info};
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout);
        try
        {
            process.Start();started?.Invoke(process.Id);deadline.Token.ThrowIfCancellationRequested();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);process.StandardInput.Close();
            var output=ReadBoundedAsync(process.StandardOutput,131072,deadline.Token);
            var errors=ReadBoundedAsync(process.StandardError,16384,deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var json=await output.ConfigureAwait(false);await errors.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)return new("Unsupported","Local text provider worker could not read this game.");
            return JsonSerializer.Deserialize<Result>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new("Unsupported","Local text provider returned no result.");
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return new("TimedOut","Local text provider timed out; OCR fallback is available.");}
        catch(OperationCanceledException){throw;}
        catch(Exception){return new("Unsupported","Local text provider is unavailable for this game.");}
        finally
        {
            // Kill only this owned worker, never the selected game or an external hook program.
            try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch{}
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);}catch{}
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader,int maximum,CancellationToken ct)
    {
        var text=new StringBuilder();var buffer=new char[4096];int length;
        while((length=await reader.ReadAsync(buffer.AsMemory(),ct).ConfigureAwait(false))>0)
        {if(text.Length+length>maximum)throw new InvalidDataException("Worker output exceeds its local bound.");text.Append(buffer,0,length);}
        return text.ToString();
    }
    // Requests enter via JSON stdin, never interpolation or Invoke-Expression. The only UIA root
    // is the explicitly selected HWND. Literal provider strings remain in memory, never on disk.
    private const string Script="""
        $ErrorActionPreference='Stop'
        [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        Add-Type -AssemblyName WindowsBase
        Add-Type -ReferencedAssemblies UIAutomationClient,UIAutomationTypes,WindowsBase -TypeDefinition @'
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Runtime.InteropServices;
        using System.Windows;
        using System.Windows.Automation;
        public static class SelectedGameText {
          [StructLayout(LayoutKind.Sequential)] struct RECT {public int Left,Top,Right,Bottom;}
          [StructLayout(LayoutKind.Sequential)] struct MONITORINFO {public int Size;public RECT Monitor,Work;public uint Flags;}
          [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
          [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
          [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
          [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
          [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT rect);
          [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
          [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h,ref MONITORINFO info);
          [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h,uint attribute,out RECT rect,int size);
          [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
          [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
          [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
          [DllImport("user32.dll")] static extern bool CloseClipboard();
          [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
          [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);
          [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
          [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
          static bool ProcessMatches(uint pid,string name) {try {using(var p=Process.GetProcessById((int)pid))return String.Equals(p.ProcessName,name,StringComparison.OrdinalIgnoreCase);}catch{return false;}}
          static bool Primary(IntPtr h) {RECT rect;if(!GetWindowRect(h,out rect))return false;RECT visible;if(DwmGetWindowAttribute(h,9,out visible,Marshal.SizeOf(typeof(RECT)))==0&&visible.Right>visible.Left&&visible.Bottom>visible.Top)rect=visible;var info=new MONITORINFO();info.Size=Marshal.SizeOf(typeof(MONITORINFO));return GetMonitorInfo(MonitorFromWindow(h,2),ref info)&&(info.Flags&1)!=0&&rect.Right>rect.Left&&rect.Bottom>rect.Top&&rect.Left>=info.Monitor.Left&&rect.Top>=info.Monitor.Top&&rect.Right<=info.Monitor.Right&&rect.Bottom<=info.Monitor.Bottom;}
          public static bool Target(long handle,uint pid,string name) {var h=new IntPtr(handle);uint actual;return handle!=0&&pid!=0&&IsWindow(h)&&IsWindowVisible(h)&&!IsIconic(h)&&GetWindowThreadProcessId(h,out actual)!=0&&actual==pid&&ProcessMatches(pid,name)&&Primary(h);}
          public static uint Sequence() {return GetClipboardSequenceNumber();}
          public static bool ClipboardOwner(uint pid,string name) {uint actual;var h=GetClipboardOwner();return h!=IntPtr.Zero&&GetWindowThreadProcessId(h,out actual)!=0&&actual==pid&&ProcessMatches(pid,name);}
          public static string Clipboard(uint pid,string name,uint sequence) {
            if(!ClipboardOwner(pid,name)||!OpenClipboard(IntPtr.Zero))return null;
            try {if(GetClipboardSequenceNumber()!=sequence||!ClipboardOwner(pid,name))return null;var h=GetClipboardData(13);if(h==IntPtr.Zero)return null;var bytes=GlobalSize(h).ToUInt64();if(bytes==0||bytes>65538)throw new InvalidOperationException("Clipboard text exceeds local bound.");var p=GlobalLock(h);if(p==IntPtr.Zero)return null;try {var text=Marshal.PtrToStringUni(p,(int)(bytes/2));var end=text.IndexOf('\0');return end<0?text:text.Substring(0,end);}finally {GlobalUnlock(h);}}
            finally {CloseClipboard();}
          }
          public static string[] VisibleText(long handle,uint pid) {
            var root=AutomationElement.FromHandle(new IntPtr(handle));if(root==null||root.Current.ProcessId!=(int)pid)return new string[0];
            RECT native;if(!GetWindowRect(new IntPtr(handle),out native))return new string[0];RECT visible;if(DwmGetWindowAttribute(new IntPtr(handle),9,out visible,Marshal.SizeOf(typeof(RECT)))==0&&visible.Right>visible.Left&&visible.Bottom>visible.Top)native=visible;var bounds=new Rect(native.Left,native.Top,native.Right-native.Left,native.Bottom-native.Top);
            var pending=new Stack<AutomationElement>();pending.Push(root);var result=new List<string>();var walker=TreeWalker.ControlViewWalker;int visited=0,total=0;
            while(pending.Count>0) {
              if(++visited>256)throw new InvalidOperationException("Provider tree exceeds local bound.");var element=pending.Pop();var current=element.Current;
              if(current.ProcessId!=(int)pid||current.IsOffscreen||current.IsPassword)continue;var rect=current.BoundingRectangle;if(rect.IsEmpty||!bounds.IntersectsWith(rect))continue;
              object pattern;bool textPattern=element.TryGetCurrentPattern(TextPattern.Pattern,out pattern);
              if(textPattern) {foreach(var range in ((TextPattern)pattern).GetVisibleRanges()){var rectangles=range.GetBoundingRectangles();bool contained=rectangles.Length>0;foreach(var rangeRect in rectangles)if(rangeRect.IsEmpty||!bounds.Contains(rangeRect))contained=false;if(!contained)continue;var text=range.GetText(32769);if(text.Length>32768||total+text.Length>32768)throw new InvalidOperationException("Provider text exceeds local bound.");if(!String.IsNullOrWhiteSpace(text)){result.Add(text);total+=text.Length;}}}
              else if(bounds.Contains(rect)&&element.TryGetCurrentPattern(ValuePattern.Pattern,out pattern)) {var value=(ValuePattern)pattern;if(value.Current.IsReadOnly){var text=value.Current.Value;if(text.IndexOf('\n')<0&&text.IndexOf('\r')<0){if(total+text.Length>32768)throw new InvalidOperationException("Provider text exceeds local bound.");if(!String.IsNullOrWhiteSpace(text)){result.Add(text);total+=text.Length;}}}}
              // TextPattern already owns its text descendants; avoid duplicate and hidden child values.
              if(textPattern)continue;
              var child=walker.GetFirstChild(element);var children=new List<AutomationElement>();while(child!=null){if(children.Count>=256)throw new InvalidOperationException("Provider tree exceeds local bound.");children.Add(child);child=walker.GetNextSibling(child);}for(int i=children.Count-1;i>=0;i--)pending.Push(children[i]);
            }
            return result.ToArray();
          }
        }
        '@
        $request=[Console]::ReadLine() | ConvertFrom-Json
        $answer=@{Status='Unsupported';Message='This game exposes no visible accessibility text.';Blocks=@();Sequence=0}
        try {
          if(-not [SelectedGameText]::Target([long]$request.Handle,[uint32]$request.ProcessId,[string]$request.ProcessName)) {$answer.Status='TargetChanged';$answer.Message='Selected game window no longer matches its PID.'}
          elseif($request.Mode -eq 'Clipboard') {
            $answer.Sequence=[SelectedGameText]::Sequence()
            if(-not [SelectedGameText]::ClipboardOwner([uint32]$request.ProducerProcessId,[string]$request.ProducerProcessName)) {$answer.Message='Clipboard is not owned by the explicitly selected external text producer.'}
            elseif($null -eq $request.PreviousSequence -or $answer.Sequence -eq [uint32]$request.PreviousSequence) {$answer.Status='NoData';$answer.Message='Waiting for new clipboard text from the selected external producer.'}
            else {$value=[SelectedGameText]::Clipboard([uint32]$request.ProducerProcessId,[string]$request.ProducerProcessName,[uint32]$answer.Sequence);if(-not [string]::IsNullOrWhiteSpace($value)){$answer.Status='Ready';$answer.Message='Explicit external clipboard bridge.';$answer.Blocks=@($value)}}
          }
          else {$answer.Blocks=@([SelectedGameText]::VisibleText([long]$request.Handle,[uint32]$request.ProcessId));if($answer.Blocks.Count -gt 0){$answer.Status='Ready';$answer.Message='Visible selected-window accessibility text.'}}
          if(-not [SelectedGameText]::Target([long]$request.Handle,[uint32]$request.ProcessId,[string]$request.ProcessName)) {$answer.Status='TargetChanged';$answer.Blocks=@();$answer.Message='Selected game target changed during the read.'}
        } catch {$answer.Status='Unsupported';$answer.Blocks=@();$answer.Message='Selected-window text provider cannot supply bounded visible text.'}
        $answer | ConvertTo-Json -Compress -Depth 5
        """;
}
