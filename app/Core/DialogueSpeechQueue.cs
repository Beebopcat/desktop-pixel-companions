using AzerothCompanion.Services;

namespace AzerothCompanion.Core;

public enum DialoguePlaybackResult { Completed, TextOnly, Cancelled, Skipped }
public enum DialogueDeliveryState { Pending, InFlight, Delivered, Cancelled, TextOnly, Skipped }
public sealed record DialogueEventKey(GameTextSource Source,string SourceInstanceId,long Sequence);
public sealed record DialogueDelivery(DialogueEventKey Key,DialogueVoiceRoute Route,long Lease,
    DialogueDeliveryState State,DateTimeOffset AcceptedAt,string? Reason=null);
public sealed record DialogueAcceptance(int Added,int TextOnly,int Duplicates,int Skipped,int Pending,bool BacklogBlocked,string? Notice);
public sealed record DialogueQueueSummary(int Pending,long Delivered,long TextOnly,long Skipped,long Cancelled);

/// <summary>Owned literal event delivery. No timers, models, playback or retries. Callers explicitly drain and acknowledge.</summary>
public sealed class DialogueSpeechQueue
{
    private readonly object gate=new();
    private readonly Queue<DialogueDelivery> pending=new();
    private readonly Queue<DialogueDelivery> history=new();
    private readonly Dictionary<(GameTextSource Source,string Instance),long> cursors=new();
    private readonly int maximumPending,maximumCharacters,maximumHistory,maximumSourceInstances;
    private DialogueDelivery? inFlight;
    private int pendingCharacters;
    private long lease;
    private long deliveredCount,textOnlyCount,skippedCount,cancelledCount;
    public DialogueSpeechQueue(int maximumPending=64,int maximumCharacters=32768,int maximumHistory=128,int maximumSourceInstances=64)
    {
        if(maximumPending<1||maximumCharacters<1||maximumHistory<1||maximumSourceInstances<1)throw new ArgumentOutOfRangeException();
        this.maximumPending=maximumPending;this.maximumCharacters=maximumCharacters;this.maximumHistory=maximumHistory;this.maximumSourceInstances=maximumSourceInstances;
    }
    public int PendingCount {get{lock(gate)return pending.Count+(inFlight==null?0:1);}}
    public bool HasPending=>PendingCount>0;
    public DialogueQueueSummary Summary()
    {lock(gate)return new(pending.Count+(inFlight==null?0:1),deliveredCount,textOnlyCount,skippedCount,cancelledCount);}
    public IReadOnlyList<DialogueDelivery> Snapshot()
    {lock(gate)return history.Concat(inFlight==null?Array.Empty<DialogueDelivery>():new[]{inFlight}).Concat(pending).ToArray();}
    public DialogueAcceptance Accept(GameTextObservation observation,IReadOnlyList<DialogueVoiceRoute> routes,
        bool narrationEnabled,DateTimeOffset now,string? textOnlyReason=null)
    {
        if(!observation.IsEventStream||string.IsNullOrWhiteSpace(observation.SourceInstanceId))
            return new(0,0,0,0,PendingCount,false,"This observation is not a connected literal event stream.");
        if(routes.Count!=observation.Lines.Count)throw new ArgumentException("Route count must match literal event count.",nameof(routes));
        lock(gate)
        {
            int added=0,textOnly=0,duplicates=0,skipped=0;bool blocked=false;string? notice=null;
            var stream=(observation.Source,observation.SourceInstanceId!);
            if(!cursors.ContainsKey(stream)&&cursors.Count>=maximumSourceInstances)
            {
                const string capacityNotice="Direct source instance limit reached; new instances are blocked until the session resets. Old cursors were retained to prevent replay.";
                foreach(var route in routes)Remember(new(new(observation.Source,observation.SourceInstanceId!,route.Sequence),route,lease,DialogueDeliveryState.Skipped,now,capacityNotice));
                return new(0,0,0,routes.Count,pending.Count+(inFlight==null?0:1),true,capacityNotice);
            }
            long previous=cursors.GetValueOrDefault(stream);
            foreach(var route in routes)
            {
                if(route.Sequence<=0){skipped++;notice="A direct text event had no valid sequence; narration skipped it.";continue;}
                if(route.Sequence<=previous){duplicates++;continue;}
                previous=route.Sequence;
                var delivery=new DialogueDelivery(new(observation.Source,observation.SourceInstanceId!,route.Sequence),route,lease,DialogueDeliveryState.Pending,now);
                if(!narrationEnabled){textOnly++;Remember(delivery with{State=DialogueDeliveryState.TextOnly,Reason=textOnlyReason??"Speech/watch was off when the event arrived; no automatic replay."});continue;}
                if(pending.Count+(inFlight==null?0:1)>=maximumPending||pendingCharacters+route.Text.Length>maximumCharacters)
                {
                    skipped++;blocked=true;notice="Dialogue backlog reached its bound; later events were visibly skipped. Pause or read the current text manually.";
                    Remember(delivery with{State=DialogueDeliveryState.Skipped,Reason=notice});continue;
                }
                pending.Enqueue(delivery);pendingCharacters+=route.Text.Length;added++;
            }
            cursors[stream]=previous;
            return new(added,textOnly,duplicates,skipped,pending.Count+(inFlight==null?0:1),blocked,notice);
        }
    }
    public bool TryBegin(out DialogueDelivery? delivery)
    {
        lock(gate)
        {
            if(inFlight!=null||pending.Count==0){delivery=null;return false;}
            delivery=pending.Dequeue() with{State=DialogueDeliveryState.InFlight};inFlight=delivery;
            return true;
        }
    }
    public bool Complete(DialogueDelivery delivery,DialoguePlaybackResult result,string? reason=null)
    {
        lock(gate)
        {
            if(inFlight==null||inFlight.Key!=delivery.Key||inFlight.Lease!=delivery.Lease||delivery.Lease!=lease)return false;
            var state=result switch{DialoguePlaybackResult.Completed=>DialogueDeliveryState.Delivered,DialoguePlaybackResult.TextOnly=>DialogueDeliveryState.TextOnly,
                DialoguePlaybackResult.Cancelled=>DialogueDeliveryState.Cancelled,_=>DialogueDeliveryState.Skipped};
            Remember(inFlight with{State=state,Reason=reason});pendingCharacters-=inFlight.Route.Text.Length;inFlight=null;return true;
        }
    }
    public int Cancel(string reason,bool resetSession=false)
    {
        lock(gate)
        {
            lease++;int cancelled=pending.Count+(inFlight==null?0:1);
            if(inFlight!=null){Remember(inFlight with{State=DialogueDeliveryState.Cancelled,Reason=reason});inFlight=null;}
            while(pending.TryDequeue(out var item))Remember(item with{State=DialogueDeliveryState.Cancelled,Reason=reason});
            pendingCharacters=0;if(resetSession){cursors.Clear();history.Clear();deliveredCount=textOnlyCount=skippedCount=cancelledCount=0;}return cancelled;
        }
    }
    private void Remember(DialogueDelivery delivery)
    {
        switch(delivery.State){case DialogueDeliveryState.Delivered:deliveredCount++;break;case DialogueDeliveryState.TextOnly:textOnlyCount++;break;
            case DialogueDeliveryState.Skipped:skippedCount++;break;case DialogueDeliveryState.Cancelled:cancelledCount++;break;}
        history.Enqueue(delivery);while(history.Count>maximumHistory)history.Dequeue();
    }
}
