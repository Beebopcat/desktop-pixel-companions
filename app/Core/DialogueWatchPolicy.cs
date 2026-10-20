using AzerothCompanion.Services;

namespace AzerothCompanion.Core;

// Keep the literal provider/export text for reading. Stability/dedup keys must never rewrite it.
public sealed class DialogueWatchPolicy
{
    private string candidate="",spoken="",offered="";
    private DateTimeOffset since;
    public GameTextObservation? Observe(GameTextObservation observation,DateTimeOffset now)
    {
        if(!observation.HasText){candidate="";return null;}
        if(observation.IsEventStream)return null; // Events use ordered FIFO, not snapshot stability.
        string key=Key(observation);
        if(key!=candidate){candidate=key;since=now;return null;}
        if(now-since<TimeSpan.FromMilliseconds(900)||key==spoken||key==offered)return null;
        offered=key;return observation;
    }
    public void Complete(GameTextObservation observation,DialoguePlaybackResult result)
    {
        if(result==DialoguePlaybackResult.Completed&&Key(observation)==offered&&candidate==offered)spoken=offered;
        // A cancelled/muted/skipped snapshot must not automatically replay on the next poll.
        // `offered` is separate from actual delivered acknowledgment.
    }
    public void Reset(){candidate="";spoken="";offered="";since=default;}
    private static string Key(GameTextObservation observation)=>observation.Source+"\0"+
        string.Join("\n",observation.Lines.Select(line=>line.SpeakerName+"\0"+line.Text));
}
