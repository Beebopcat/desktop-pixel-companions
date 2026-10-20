namespace AzerothCompanion.Services;

public sealed class AccessibleTextService:IGameTextReader
{
    private readonly TimeSpan timeout;
    private readonly Func<DateTimeOffset> clock;
    public GameTextSource Source=>GameTextSource.Accessibility;
    public AccessibleTextService(TimeSpan? timeout=null):this(timeout,()=>DateTimeOffset.UtcNow){}
    internal AccessibleTextService(TimeSpan? timeout,Func<DateTimeOffset> clock)
    {this.timeout=timeout??TimeSpan.FromSeconds(4);if(this.timeout<=TimeSpan.Zero||this.timeout>TimeSpan.FromSeconds(10))throw new ArgumentOutOfRangeException(nameof(timeout));this.clock=clock;}
    public async Task<GameTextObservation> ReadAsync(WindowChoice target,CancellationToken cancellationToken=default)
    {
        var result=await AccessibleTextWorker.ReadAsync(new("Accessibility",target.Handle.ToInt64(),target.ProcessId,target.ProcessName),timeout,cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if(result.Status!="Ready")return GameTextObservation.Empty(Source,ParseStatus(result.Status),result.Message,clock());
        return GameTextObservation.FromLines(Source,(result.Blocks??[]).SelectMany(text=>GameTextParsing.LiteralLines(text)),clock(),GameTextExactness.ProviderText,result.Message);
    }
    internal static GameTextStatus ParseStatus(string value)=>Enum.TryParse<GameTextStatus>(value,out var status)?status:GameTextStatus.Unsupported;
}
