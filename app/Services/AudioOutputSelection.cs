using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AzerothCompanion.Services;

/// <summary>Shares the player's explicit output selection without changing Windows defaults.</summary>
internal static class AudioOutputSelection
{
    internal const string SettingsPath=@"C:\AI_Workspace\_scripts\kokoro-hotkey\audio-output.json";
    internal sealed record Output(uint DeviceId,string Name);

    internal static string? ReadSelection(string? path=null)
    {
        path??=SettingsPath;
        if(!File.Exists(path))return null;
        using var document=JsonDocument.Parse(File.ReadAllText(path));
        var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("schemaVersion",out var version)||
            version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out var schema)||schema!=1||
            !root.TryGetProperty("outputDeviceName",out var name)||name.ValueKind!=JsonValueKind.String||
            string.IsNullOrWhiteSpace(name.GetString())||name.GetString()!.Length>200||
            !root.TryGetProperty("hostApi",out var host)||host.GetString()!="MME")
            throw new InvalidDataException("Selected audio output settings are invalid. Playback stopped; no substitute output was used.");
        return name.GetString();
    }

    /// <summary>Raised when the saved device is missing and playback uses the Windows default instead. Never silent.</summary>
    internal static event Action<string>? FallbackUsed;
    internal static Output ResolveForPlayback()
    {
        string? selected=null;
        try{selected=ReadSelection();return Resolve(selected,ListOutputs());}
        catch(Exception error) when(error is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            FallbackUsed?.Invoke((selected==null?"The saved voice output could not be read":"\""+selected+"\" is not connected")+" - playing on the Windows default output instead. Pick a device under Play voice on.");
            return new(unchecked((uint)-1),"Windows default output");
        }
    }
    internal static string? SavedName(){try{return ReadSelection();}catch{return null;}}
    /// <summary>null = Windows default (the saved selection is removed). Shared with Kokoro Player; Windows' own defaults are never changed.</summary>
    internal static void Save(string? deviceName)
    {
        if(string.IsNullOrWhiteSpace(deviceName)){try{if(File.Exists(SettingsPath))File.Delete(SettingsPath);}catch(IOException){}return;}
        File.WriteAllText(SettingsPath,JsonSerializer.Serialize(new{schemaVersion=1,outputDeviceName=deviceName,hostApi="MME"}));
    }
    internal static Output Resolve(string? selected,IReadOnlyList<Output> outputs)
    {
        if(selected==null)return new(unchecked((uint)-1),"Windows default output");
        // WinMM limits friendly names to 31 characters. Long names may match that
        // prefix only when exactly one installed output has it; ambiguity fails closed.
        var matches=outputs.Where(output=>output.Name.Equals(selected,StringComparison.OrdinalIgnoreCase)||
            selected.Length>31&&output.Name.Length==31&&selected.StartsWith(output.Name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1)throw new InvalidOperationException(matches.Length==0?
            "Selected headphones are unavailable: "+selected+". Playback stopped; no substitute output was used.":
            "Selected headphones are ambiguous. Playback stopped; choose a unique output.");
        return matches[0];
    }

    internal static IReadOnlyList<Output> ListOutputs()
    {
        var outputs=new List<Output>();
        for(uint index=0;index<waveOutGetNumDevs();index++)
            if(waveOutGetDevCaps((UIntPtr)index,out var capabilities,(uint)Marshal.SizeOf<WaveCapabilities>())==0&&capabilities.Channels>0)
                outputs.Add(new(index,capabilities.Name));
        return outputs;
    }

    internal static object Verify()
    {
        var outputs=ListOutputs();var selected=ReadSelection();var resolved=Resolve(selected,outputs);
        var fixture=new Output[]{new(0,"Realtek headphones"),new(1,"Bose QC headphones")};
        if(Resolve("Bose QC headphones",fixture).DeviceId!=1)throw new InvalidOperationException("Named output routing failed.");
        bool missingRejected=false,duplicatesRejected=false;
        try{Resolve("Missing headphones",fixture);}catch(InvalidOperationException){missingRejected=true;}
        try{Resolve("Bose QC headphones",[new(0,"Bose QC headphones"),new(1,"BOSE QC HEADPHONES")]);}catch(InvalidOperationException){duplicatesRejected=true;}
        if(!missingRejected||!duplicatesRejected)throw new InvalidOperationException("Missing or ambiguous output did not fail closed.");
        return new{timeUtc=DateTimeOffset.UtcNow,allPassed=true,selectedOutputDeviceName=selected,resolvedOutput=resolved,
            outputs,checks=new[]{"Selected output resolves by friendly name","Missing output fails closed","Case-insensitive duplicate output fails closed"},
            speakerPlayback=false,microphoneRecording=false,gpuInference=false,windowsDefaultsChanged=false};
    }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WaveCapabilities
    {
        public ushort Manufacturer,Product;public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;
        public uint Formats;public ushort Channels,Reserved;public uint Support;
    }
    [DllImport("winmm.dll")]private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll",CharSet=CharSet.Unicode,EntryPoint="waveOutGetDevCapsW")]
    private static extern uint waveOutGetDevCaps(UIntPtr device,out WaveCapabilities capabilities,uint size);
}
