using System.Diagnostics;
using System.Runtime.InteropServices;
namespace AzerothCompanion.Services;
/// <summary>Owns its waveOut handle. Cancellation resets playback on the audio worker, never the UI thread.</summary>
public sealed class OwnedAudioPlayer : IDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly object stateGate=new();
    private readonly SpeechLevelNotifications speechLevels=new();
    private CancellationTokenSource? playback;
    private long generation;
    private volatile bool playing;
    public bool IsPlaying=>playing;
    /// <summary>Recent played PCM RMS, normalized to 0..1. Notifications run on a worker; marshal UI updates.</summary>
    public event Action<float>? SpeechLevelChanged
    {
        add=>speechLevels.Changed+=value;
        remove=>speechLevels.Changed-=value;
    }
    public void Stop()
    {
        long owner;
        lock(stateGate)
        {
            owner=Interlocked.Increment(ref generation);
            playback?.Cancel();
            playing=false;
        }
        speechLevels.SetOwner(owner,false);
    }
    public Task<bool> PlayAsync(byte[] float32Pcm,int sampleRate,CancellationToken ct)
    {
        long owner;
        lock(stateGate)
        {
            owner=Interlocked.Increment(ref generation);playback?.Cancel();playing=false;
        }
        speechLevels.SetOwner(owner,false);
        // Native setup, PCM conversion, and cleanup cannot occupy the WPF input thread.
        return Task.Run(()=>PlayCoreAsync(float32Pcm,sampleRate,owner,ct),ct);
    }
    private async Task<bool> PlayCoreAsync(byte[] float32Pcm,int sampleRate,long owner,CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
        IntPtr handle=IntPtr.Zero,header=IntPtr.Zero,data=IntPtr.Zero;bool prepared=false;
        try
        {
            lock(stateGate){if(owner!=Interlocked.Read(ref generation))return false;playback=linked;}
            linked.Token.ThrowIfCancellationRequested();
            if(sampleRate!=24000||float32Pcm.Length==0||float32Pcm.Length%4!=0)throw new InvalidDataException("Unsupported narration audio format.");
            var samples=new short[float32Pcm.Length/4];
            // Loudness: some voices render very quietly (whisper/ASMR peaks near 0.09). Lift quiet audio toward a
            // comfortable speaking level without clipping; louder audio is left unchanged.
            double sum=0,peak=0;int count=samples.Length;
            for(int i=0;i<count;i++)
            {
                float v=BitConverter.ToSingle(float32Pcm,i*4);
                if(!float.IsFinite(v))throw new InvalidDataException("Narration contains invalid samples.");
                sum+=(double)v*v;peak=Math.Max(peak,Math.Abs(v));
            }
            double rms=Math.Sqrt(sum/Math.Max(1,count)),gain=1;
            if(rms>1e-5&&rms<0.10)gain=Math.Min(Math.Min(0.10/rms,0.92/Math.Max(peak,1e-5)),12.0);
            for(int i=0;i<samples.Length;i++)
            {
                if((i&16383)==0)linked.Token.ThrowIfCancellationRequested();
                double s=BitConverter.ToSingle(float32Pcm,i*4)*gain;
                samples[i]=(short)(Math.Clamp(s,-1,1)*32767);
            }
            var format=new WaveFormat{FormatTag=1,Channels=1,SamplesPerSec=(uint)sampleRate,AvgBytesPerSec=(uint)sampleRate*2,BlockAlign=2,BitsPerSample=16};
            linked.Token.ThrowIfCancellationRequested();if(owner!=Interlocked.Read(ref generation))return false;
            var output=AudioOutputSelection.ResolveForPlayback();
            Check(waveOutOpen(out handle,output.DeviceId,ref format,IntPtr.Zero,IntPtr.Zero,0));
            data=Marshal.AllocHGlobal(samples.Length*2);Marshal.Copy(samples,0,data,samples.Length);
            header=Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());Marshal.StructureToPtr(new WaveHeader{Data=data,BufferLength=(uint)samples.Length*2},header,false);
            Check(waveOutPrepareHeader(handle,header,(uint)Marshal.SizeOf<WaveHeader>()));prepared=true;
            lock(stateGate)
            {
                linked.Token.ThrowIfCancellationRequested();if(owner!=Interlocked.Read(ref generation))return false;
                // Stop uses this same short critical section: once it returns, an old
                // owner cannot submit a buffer after the stop request.
                Check(waveOutWrite(handle,header,(uint)Marshal.SizeOf<WaveHeader>()));
                playing=true;
            }
            var playbackClock=Stopwatch.StartNew();
            speechLevels.SetOwner(owner,true);
            linked.Token.ThrowIfCancellationRequested();
            while((Marshal.PtrToStructure<WaveHeader>(header).Flags&1)==0)
            {
                linked.Token.ThrowIfCancellationRequested();
                var position=new WavePosition{Type=2}; // TIME_SAMPLES; a driver can return another type.
                var positionResult=waveOutGetPosition(handle,ref position,(uint)Marshal.SizeOf<WavePosition>());
                int playedSamples=SpeechAmplitude.SampleOffset(positionResult==0?position.Type:0,
                    position.Value,sampleRate,samples.Length,playbackClock.Elapsed.TotalSeconds);
                speechLevels.Publish(owner,SpeechAmplitude.Rms(samples,playedSamples,sampleRate/20));
                await Task.Delay(50,linked.Token).ConfigureAwait(false);
            }
            return !linked.IsCancellationRequested && owner == Interlocked.Read(ref generation);
        }
        finally
        {
            playing=false;
            speechLevels.SetOwner(owner,false);
            if(handle!=IntPtr.Zero)
            {
                waveOutReset(handle);
                if(prepared)waveOutUnprepareHeader(handle,header,(uint)Marshal.SizeOf<WaveHeader>());
                waveOutClose(handle);
            }
            lock(stateGate){if(ReferenceEquals(playback,linked))playback=null;}
            if(header!=IntPtr.Zero)Marshal.FreeHGlobal(header);if(data!=IntPtr.Zero)Marshal.FreeHGlobal(data);
            gate.Release();
        }
    }
    static void Check(uint code){if(code!=0)throw new InvalidOperationException("Audio output is unavailable (Windows audio error "+code+").");}
    public void Dispose()=>Stop();
    [StructLayout(LayoutKind.Sequential,Pack=2)]private struct WaveFormat{public ushort FormatTag,Channels;public uint SamplesPerSec,AvgBytesPerSec;public ushort BlockAlign,BitsPerSample,ExtraSize;}
    [StructLayout(LayoutKind.Sequential)]private struct WaveHeader{public IntPtr Data;public uint BufferLength,BytesRecorded;public UIntPtr User;public uint Flags,Loops;public IntPtr Next;public UIntPtr Reserved;}
    // MMTIME consists of a UINT followed by an eight-byte union, on both x86 and x64.
    [StructLayout(LayoutKind.Explicit,Size=12)]private struct WavePosition
    {
        [FieldOffset(0)]public uint Type;
        [FieldOffset(4)]public uint Value;
    }
    [DllImport("winmm.dll")]private static extern uint waveOutOpen(out IntPtr h,uint device,ref WaveFormat format,IntPtr callback,IntPtr instance,uint flags);
    [DllImport("winmm.dll")]private static extern uint waveOutPrepareHeader(IntPtr h,IntPtr header,uint size);
    [DllImport("winmm.dll")]private static extern uint waveOutWrite(IntPtr h,IntPtr header,uint size);
    [DllImport("winmm.dll")]private static extern uint waveOutGetPosition(IntPtr h,ref WavePosition position,uint size);
    [DllImport("winmm.dll")]private static extern uint waveOutReset(IntPtr h);
    [DllImport("winmm.dll")]private static extern uint waveOutUnprepareHeader(IntPtr h,IntPtr header,uint size);
    [DllImport("winmm.dll")]private static extern uint waveOutClose(IntPtr h);
}

internal static class SpeechAmplitude
{
    internal static int SampleOffset(uint type,uint value,int sampleRate,int sampleCount,double elapsedSeconds)
    {
        double samples=type switch
        {
            2=>value,                       // TIME_SAMPLES
            4=>value/2d,                    // TIME_BYTES for the owned mono PCM16 stream
            1=>value*(double)sampleRate/1000,// TIME_MS
            _=>elapsedSeconds*sampleRate    // Honest elapsed-time estimate if the driver cannot report position
        };
        if(!double.IsFinite(samples))return 0;
        return (int)Math.Clamp(samples,0,Math.Max(0,sampleCount));
    }
    internal static float Rms(ReadOnlySpan<short> samples,int playedSamples,int windowSamples)
    {
        int end=Math.Clamp(playedSamples,0,samples.Length);
        int start=Math.Max(0,end-Math.Max(1,windowSamples));
        if(end==start)return 0;
        double energy=0;
        for(int index=start;index<end;index++)
        {
            double sample=samples[index]/(double)short.MaxValue;energy+=sample*sample;
        }
        return (float)Math.Clamp(Math.Sqrt(energy/(end-start)),0,1);
    }
}

// One worker serializes callbacks, coalescing level samples while preserving a reset
// before the next owner's levels. No subscriber or native call runs under this gate.
internal sealed class SpeechLevelNotifications
{
    private readonly object levelGate=new();
    private long currentOwner;
    private bool active,dispatching,resetPending;
    private (long Owner,float Level)? pendingLevel;
    private Task deliveryTask=Task.CompletedTask;
    internal event Action<float>? Changed;
    internal void SetOwner(long owner,bool isPlaying)
    {
        lock(levelGate)
        {
            if(owner<currentOwner)return;
            if(owner!=currentOwner||!isPlaying)pendingLevel=null;
            currentOwner=owner;active=isPlaying;resetPending=true;EnsureDispatcherLocked();
        }
    }
    internal void Publish(long owner,float level)
    {
        lock(levelGate)
        {
            if(owner!=currentOwner||!active)return;
            pendingLevel=(owner,float.IsFinite(level)?Math.Clamp(level,0,1):0);EnsureDispatcherLocked();
        }
    }
    internal Task WaitForDeliveryAsync(){lock(levelGate)return deliveryTask;}
    private void EnsureDispatcherLocked()
    {
        if(dispatching)return;
        dispatching=true;deliveryTask=Task.Run(Dispatch);
    }
    private void Dispatch()
    {
        while(true)
        {
            (long Owner,float Level) item;
            lock(levelGate)
            {
                if(resetPending){item=(currentOwner,0);resetPending=false;}
                else if(pendingLevel is { } pending){item=pending;pendingLevel=null;}
                else{dispatching=false;return;}
                if(item.Owner!=currentOwner||(item.Level>0&&!active))continue;
            }
            var subscribers=Changed;
            if(subscribers==null)continue;
            foreach(Action<float> subscriber in subscribers.GetInvocationList())
            {
                lock(levelGate)
                    if(item.Owner!=currentOwner||(item.Level>0&&!active))break;
                try{subscriber(item.Level);}catch(Exception){ /* An avatar callback cannot interrupt audio cleanup. */ }
            }
        }
    }
}
