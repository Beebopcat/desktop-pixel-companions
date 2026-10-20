namespace AzerothCompanion.Services;

// This bridge is created only for explicit per-session consent. It never polls on its own,
// ignores existing clipboard data, and reads new text only from one selected producer PID/name.
public sealed class GameTextClipboardReader:IGameTextReader
{
    private readonly GameTextClipboardProfile profile;
    private readonly TimeSpan timeout;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim mutex=new(1,1);
    private uint? sequence;
    private GameTextObservation? previous;
    private readonly string sourceInstanceId=Guid.NewGuid().ToString("N");
    private long deliverySequence;
    private long? upstreamSequence;
    public GameTextSource Source=>GameTextSource.ClipboardBridge;
    public GameTextClipboardReader(GameTextClipboardProfile profile,TimeSpan? timeout=null):this(profile,timeout,()=>DateTimeOffset.UtcNow){}
    internal GameTextClipboardReader(GameTextClipboardProfile profile,TimeSpan? timeout,Func<DateTimeOffset> clock)
    {
        if(!profile.Enabled||profile.TargetProcessId==0||profile.ProducerProcessId==0||string.IsNullOrWhiteSpace(profile.ProducerProcessName))throw new ArgumentException("Clipboard bridge requires explicit consent and selected game/producer PIDs.",nameof(profile));
        this.profile=profile;this.timeout=timeout??TimeSpan.FromSeconds(4);if(this.timeout<=TimeSpan.Zero||this.timeout>TimeSpan.FromSeconds(10))throw new ArgumentOutOfRangeException(nameof(timeout));this.clock=clock;
    }
    public async Task<GameTextObservation> ReadAsync(WindowChoice target,CancellationToken cancellationToken=default)
    {
        if(target.ProcessId!=profile.TargetProcessId)return GameTextObservation.Empty(Source,GameTextStatus.Unsupported,"Clipboard consent belongs to a different selected game.",clock());
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result=await AccessibleTextWorker.ReadAsync(new("Clipboard",target.Handle.ToInt64(),target.ProcessId,target.ProcessName,profile.ProducerProcessId,profile.ProducerProcessName,sequence),timeout,cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if(result.Status is "Ready" or "NoData")sequence=result.Sequence;
            else previous=null;
            if(result.Status=="NoData")return GameTextObservation.Empty(Source,GameTextStatus.ConnectedWaiting,"Waiting for new text from the explicitly selected clipboard producer.",clock()) with{SourceInstanceId=sourceInstanceId,IsEventStream=true};
            if(result.Status!="Ready")return GameTextObservation.Empty(Source,AccessibleTextService.ParseStatus(result.Status),result.Message,clock());
            var text=string.Join("\n",result.Blocks??[]);
            previous=GameTextObservation.FromLines(Source,GameTextParsing.StreamLines(text,profile.Format,ref deliverySequence,ref upstreamSequence),clock(),GameTextExactness.ExternalText,"Explicit external clipboard text; upstream hook/OCR correctness is not verified.") with{SourceInstanceId=sourceInstanceId,IsEventStream=true};
            if(!previous.HasText)return previous with{Status=GameTextStatus.ConnectedWaiting};
            return previous;
        }
        catch(System.Text.Json.JsonException){return GameTextObservation.Empty(Source,GameTextStatus.Unsupported,"Clipboard export is not valid dialogue JSONL.",clock());}
        finally{mutex.Release();}
    }
}
