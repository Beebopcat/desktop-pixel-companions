namespace AzerothCompanion.Controls;

public readonly record struct CompanionPcMotionFrame(int ActorFrame,int ScreenFrame,bool Playing);

/// <summary>One explicit twenty-two second scene at the current three-times virtual clock.
/// Three authored rear-view poses are held rather than rapidly toggled; the separate invented
/// game uses four authored display states. No body transformations or input automation.</summary>
public static class CompanionPcMotion
{
    public static CompanionPcMotionFrame Sample(double realSeconds)
    {
        if(!double.IsFinite(realSeconds))throw new ArgumentOutOfRangeException(nameof(realSeconds));
        bool playing=realSeconds is >=5.28 and <18.92;
        double elapsed=Math.Max(0,realSeconds-5.28);
        return new((int)(elapsed/1.25)%12,2+(int)(elapsed/.65)%4,playing);
    }
}
