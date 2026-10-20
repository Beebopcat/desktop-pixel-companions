namespace AzerothCompanion.Services;

public enum GameTextSource { None, Accessibility, DialogueFile, ClipboardBridge, VerifiedHook }
public enum GameTextStatus { Ready, Unchanged, NoData, Unsupported, Stale, TargetChanged, TimedOut, ConnectedWaiting, Unavailable, Connecting }
public enum GameTextExactness { None, ProviderText, ExportedText, ExternalText }
public enum GameTextFileFormat { PlainLines, JsonLines }
public enum DialogueVoiceRole { Neutral, Male, Female }
public sealed record GameTextLine(string Text,string? SpeakerName=null,long Sequence=0,DialogueVoiceRole VoiceRole=DialogueVoiceRole.Neutral);
public sealed record GameTextObservation(GameTextSource Source,string Text,IReadOnlyList<GameTextLine> Lines,
    DateTimeOffset Timestamp,GameTextExactness Exactness,GameTextStatus Status,string Message)
{
    public string? SourceInstanceId { get; init; }
    public bool IsEventStream { get; init; }
    public bool BlocksOcr => Status is GameTextStatus.ConnectedWaiting or GameTextStatus.Connecting;
    public bool HasText=>(Status is GameTextStatus.Ready or GameTextStatus.Unchanged)&&!string.IsNullOrWhiteSpace(Text);
    public static GameTextObservation Empty(GameTextSource source,GameTextStatus status,string message,DateTimeOffset? at=null)=>
        new(source,"",Array.Empty<GameTextLine>(),at??DateTimeOffset.UtcNow,GameTextExactness.None,status,message);
    public static GameTextObservation FromLines(GameTextSource source,IEnumerable<GameTextLine> lines,
        DateTimeOffset at,GameTextExactness exactness,string message)
    {
        // Exact ordinal comparison: no spelling, spacing, punctuation, or speaker inference.
        var seen=new HashSet<(string Text,string? Speaker,long Sequence,DialogueVoiceRole Role)>();
        var unique=lines.Where(line=>!string.IsNullOrWhiteSpace(line.Text)&&seen.Add((line.Text,line.SpeakerName,
            source is GameTextSource.DialogueFile or GameTextSource.ClipboardBridge or GameTextSource.VerifiedHook?line.Sequence:0,line.VoiceRole))).ToArray();
        return unique.Length==0?Empty(source,GameTextStatus.NoData,message,at):
            new(source,string.Join("\n",unique.Select(line=>line.Text)),unique,at,exactness,GameTextStatus.Ready,message);
    }
}
public sealed record GameTextFileProfile(string AllowedPath,string TargetProcessName,
    GameTextFileFormat Format=GameTextFileFormat.PlainLines,bool ReadExisting=false);
public sealed record GameTextClipboardProfile(uint TargetProcessId,uint ProducerProcessId,string ProducerProcessName,
    bool Enabled=false,GameTextFileFormat Format=GameTextFileFormat.PlainLines);
public sealed record GameTextOptions(bool AccessibilityEnabled=true,GameTextFileProfile? FileProfile=null,
    GameTextClipboardProfile? ClipboardProfile=null,TimeSpan? MaximumAge=null,TimeSpan? AccessibilityTimeout=null);
public interface IGameTextReader
{
    GameTextSource Source {get;}
    Task<GameTextObservation> ReadAsync(WindowChoice target,CancellationToken cancellationToken=default);
}

internal static class GameTextParsing
{
    internal const int MaximumCharacters=32768;
    internal static IEnumerable<GameTextLine> LiteralLines(string text,long firstSequence=1)
    {
        using var reader=new System.IO.StringReader(text);string? line;long sequence=firstSequence;
        while((line=reader.ReadLine())!=null)yield return new(line,null,sequence++);
    }
    internal static IEnumerable<GameTextLine> ExportLines(string text,GameTextFileFormat format)
    {
        if(text.Length>MaximumCharacters)throw new System.Text.Json.JsonException("Dialogue export exceeds the local bound.");
        if(format==GameTextFileFormat.PlainLines)return LiteralLines(text).ToArray();
        var result=new List<GameTextLine>();
        foreach(var raw in LiteralLines(text))
        {
            if(string.IsNullOrWhiteSpace(raw.Text))continue;
            using var json=System.Text.Json.JsonDocument.Parse(raw.Text);var root=json.RootElement;
            if(root.ValueKind!=System.Text.Json.JsonValueKind.Object||!root.TryGetProperty("text",out var body)||body.ValueKind!=System.Text.Json.JsonValueKind.String)
                throw new System.Text.Json.JsonException("Dialogue JSONL requires a text string.");
            string? speaker=null;long sequence=raw.Sequence;
            var keys=new HashSet<string>(StringComparer.Ordinal);foreach(var property in root.EnumerateObject())if(!keys.Add(property.Name))throw new System.Text.Json.JsonException("Dialogue JSONL has duplicate metadata fields.");
            if(root.TryGetProperty("speaker",out var name))
            {
                if(name.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.String))throw new System.Text.Json.JsonException("Speaker metadata must be a string.");
                speaker=name.ValueKind==System.Text.Json.JsonValueKind.String?name.GetString():null;
                if(speaker?.Length>256)throw new System.Text.Json.JsonException("Speaker metadata exceeds its local bound.");
            }
            if(root.TryGetProperty("sequence",out var id)&&(!id.TryGetInt64(out sequence)||sequence<0))throw new System.Text.Json.JsonException("Sequence metadata must be a nonnegative integer.");
            var literal=body.GetString()??"";
            if(literal.Length>MaximumCharacters)throw new System.Text.Json.JsonException("Dialogue line exceeds the local bound.");
            var role=DialogueVoiceRole.Neutral;
            if(root.TryGetProperty("voiceRole",out var roleValue))
            {
                if(roleValue.ValueKind is not (System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Null))
                    throw new System.Text.Json.JsonException("Voice-role metadata must be a string.");
                role=roleValue.GetString()?.ToLowerInvariant() switch
                {"male"=>DialogueVoiceRole.Male,"female"=>DialogueVoiceRole.Female,_=>DialogueVoiceRole.Neutral};
            }
            result.Add(new(literal,speaker,sequence,role));
        }
        return result;
    }
    internal static GameTextLine[] StreamLines(string text,GameTextFileFormat format,ref long delivery,ref long? upstream)
    {
        var result=ExportLines(text,format).ToArray(); long? next=upstream;
        if(format==GameTextFileFormat.JsonLines)
        {
            foreach(var raw in LiteralLines(text).Where(x=>!string.IsNullOrWhiteSpace(x.Text)))
            {
                using var document=System.Text.Json.JsonDocument.Parse(raw.Text);
                if(document.RootElement.TryGetProperty("sequence",out var value))
                {
                    long id=value.GetInt64();
                    if(next is { } previous&&id<=previous)throw new System.Text.Json.JsonException("Replay or out-of-order exported sequence.");
                    next=id;
                }
            }
        }
        long counter=delivery; result=result.Select(line=>line with{Sequence=++counter}).ToArray();
        delivery=counter;upstream=next;return result;
    }
}
