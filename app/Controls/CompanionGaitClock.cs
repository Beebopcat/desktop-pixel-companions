namespace AzerothCompanion.Controls;

/// <summary>One adjacent stride pose per presentation. A delayed UI tick never
/// jumps over authored footfalls; lost time is dropped rather than replayed in a burst.</summary>
internal sealed class CompanionGaitClock
{
    private const double FrameSeconds=1.0/6.5; // 8 poses = one ~1.2 s stride; matches ~65 px/s roaming speed (was a too-fast shuffle)
    private int frame;
    private double next;
    private bool started;
    internal void Reset()=>started=false;
    internal int Sample(double virtualSeconds)
    {
        if(!double.IsFinite(virtualSeconds))throw new ArgumentOutOfRangeException(nameof(virtualSeconds));
        if(!started){started=true;frame=0;next=virtualSeconds+FrameSeconds;return frame;}
        if(virtualSeconds<next)return frame;
        frame=(frame+1)%8;
        next=virtualSeconds-next>FrameSeconds*2?virtualSeconds+FrameSeconds:next+FrameSeconds;
        return frame;
    }
}
