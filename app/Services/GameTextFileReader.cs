using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AzerothCompanion.Services;

/// <summary>Explicit local subtitle/export file, read only. Never discovers files or watches directories.</summary>
public sealed class GameTextFileReader:IGameTextReader
{
    private const int MaximumReadBytes=65536;
    private readonly GameTextFileProfile profile;
    private readonly string allowedPath;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim mutex=new(1,1);
    private Decoder decoder=new UTF8Encoding(false,true).GetDecoder();
    private string partial="";
    private long position,sequence;
    private long? upstreamSequence;
    private DateTime created;
    private bool initialized;
    private string? fileIdentity;
    private string sourceInstanceId=Guid.NewGuid().ToString("N");
    public GameTextSource Source=>GameTextSource.DialogueFile;
    public GameTextFileReader(GameTextFileProfile profile):this(profile,()=>DateTimeOffset.UtcNow){}
    internal GameTextFileReader(GameTextFileProfile profile,Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if(string.IsNullOrWhiteSpace(profile.TargetProcessName)||!Path.IsPathFullyQualified(profile.AllowedPath)||
            profile.AllowedPath.StartsWith("\\",StringComparison.Ordinal)||profile.AllowedPath.IndexOfAny(['*','?'])>=0||profile.AllowedPath.IndexOf(':',2)>=0)
            throw new ArgumentException("Select one fully qualified local subtitle/export file and its game process name.",nameof(profile));
        this.profile=profile;allowedPath=Path.GetFullPath(profile.AllowedPath);this.clock=clock;
    }
    public async Task<GameTextObservation> ReadAsync(WindowChoice target,CancellationToken cancellationToken=default)
    {
        if(!string.Equals(profile.TargetProcessName,target.ProcessName,StringComparison.OrdinalIgnoreCase))return GameTextObservation.Empty(Source,GameTextStatus.Unsupported,"Selected export belongs to a different game process.",clock());
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{return await Task.Run(()=>ReadCoreAsync(cancellationToken),CancellationToken.None).ConfigureAwait(false);}
        catch(OperationCanceledException){throw;}
        catch(DecoderFallbackException){ResetStream();return GameTextObservation.Empty(Source,GameTextStatus.Unsupported,"Selected export is not strict UTF-8 text.",clock());}
        catch(System.Text.Json.JsonException){return GameTextObservation.Empty(Source,GameTextStatus.Unavailable,"Selected export is malformed or repeats an upstream dialogue sequence.",clock());}
        catch(Exception) {return GameTextObservation.Empty(Source,GameTextStatus.Unavailable,"Selected local export is unavailable or violates the read-only file scope.",clock());}
        finally{mutex.Release();}
    }
    private async Task<GameTextObservation> ReadCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ValidateLocalFile();
        using var stream=new FileStream(allowedPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.Asynchronous|FileOptions.SequentialScan);
        ValidateOpenedFile(stream.SafeFileHandle);ct.ThrowIfCancellationRequested();
        var creation=File.GetCreationTimeUtc(allowedPath);string openedIdentity=OpenedIdentity(stream.SafeFileHandle);
        if(!initialized){initialized=true;created=creation;fileIdentity=openedIdentity;position=profile.ReadExisting?0:stream.Length;}
        else if(creation!=created||fileIdentity!=openedIdentity||stream.Length<position){ResetStream();initialized=true;created=creation;fileIdentity=openedIdentity;position=0;}
        long available=stream.Length-position;
        if(available>MaximumReadBytes){return GameTextObservation.Empty(Source,GameTextStatus.Unavailable,"Selected export has more new text than the bounded local reader supports.",clock());}
        if(available==0)return Waiting("Waiting for new lines in the explicitly selected game export.");
        stream.Position=position;var bytes=new byte[(int)available];int total=0;
        while(total<bytes.Length){var count=await stream.ReadAsync(bytes.AsMemory(total),ct).ConfigureAwait(false);if(count==0)break;total+=count;}
        ct.ThrowIfCancellationRequested();
        var characters=new char[total+2];var countCharacters=decoder.GetChars(bytes,0,total,characters,0,flush:false);position+=total;
        var combined=partial+new string(characters,0,countCharacters);int end=combined.LastIndexOf('\n');
        if(combined.Length>GameTextParsing.MaximumCharacters){partial="";return GameTextObservation.Empty(Source,GameTextStatus.Unavailable,"Selected export exceeds the literal text bound.",clock());}
        if(end<0){partial=combined;return Waiting("Waiting for a complete export line.");}
        var complete=combined[..(end+1)];partial=combined[(end+1)..];
        // UTF-8 BOM is transport metadata only. Do not trim dialogue whitespace or punctuation.
        if(position==total&&complete.StartsWith('\uFEFF'))complete=complete[1..];
        var parsed=GameTextParsing.StreamLines(complete,profile.Format,ref sequence,ref upstreamSequence);
        var observation=GameTextObservation.FromLines(Source,parsed,clock(),GameTextExactness.ExportedText,"Literal text from the explicitly selected local export; upstream format controls speaker metadata.");
        ct.ThrowIfCancellationRequested();
        return observation.HasText?observation with{SourceInstanceId=sourceInstanceId,IsEventStream=true}:Waiting("Waiting for a new literal export line.");
    }
    private GameTextObservation Waiting(string message)=>GameTextObservation.Empty(Source,GameTextStatus.ConnectedWaiting,message,clock()) with{SourceInstanceId=sourceInstanceId,IsEventStream=true};
    private void ResetStream(){position=0;sequence=0;upstreamSequence=null;partial="";decoder=new UTF8Encoding(false,true).GetDecoder();fileIdentity=null;initialized=false;sourceInstanceId=Guid.NewGuid().ToString("N");}
    private static string OpenedIdentity(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandle(handle,out var info))throw new IOException("Export file identity could not be verified.");
        return $"{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }
    [StructLayout(LayoutKind.Sequential)] private struct ByHandleInfo
    {
        public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,LastAccess,LastWrite;
        public uint VolumeSerial,SizeHigh,SizeLow,Links,FileIndexHigh,FileIndexLow;
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out ByHandleInfo info);
    private void ValidateLocalFile()
    {
        var root=Path.GetPathRoot(allowedPath)!;if(new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown)throw new IOException("Network export paths are disabled.");
        for(string? current=allowedPath;current!=null;current=Path.GetDirectoryName(current))
        {if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse export paths are disabled.");if(string.Equals(current,root,StringComparison.OrdinalIgnoreCase))break;}
    }
    private void ValidateOpenedFile(SafeFileHandle handle)
    {
        var path=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(handle,path,(uint)path.Capacity,0);
        if(length==0||length>=path.Capacity)throw new IOException("Export handle path could not be verified.");
        var final=path.ToString();if(final.StartsWith("\\\\?\\",StringComparison.Ordinal))final=final[4..];
        if(!string.Equals(final,allowedPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Opened export resolves outside its exact selected path.");
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
}
