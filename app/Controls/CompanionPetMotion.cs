namespace AzerothCompanion.Controls;

public sealed record CompanionPetMotionFrame(int ArtFrame,double OffsetX,double JumpHeight,bool ContactRequested,double ContactBlend);

/// <summary>Continuous real-time animal motion using four authored poses at ONE constant scale.
/// A leap has a short ballistic arc, never a static airborne hold. Body posing stays with the actor.</summary>
public static class CompanionPetMotion
{
    public static CompanionPetMotionFrame Sample(CompanionPerformanceFrame frame,double referenceHandY)
    {
        double t=frame.RealElapsedSeconds;string id=frame.ClipId??"";
        bool feed=id.EndsWith(".feed",StringComparison.Ordinal),settle=id.EndsWith(".settle",StringComparison.Ordinal),play=id.EndsWith(".play",StringComparison.Ordinal);
        double approach=Math.Clamp((t-3.8)/2.2,0,1);approach=approach*approach*(3-2*approach);
        double offset=(1-approach)*.12;
        // Each pass includes anticipation, a finite jump, landing and a long grounded hold.
        double jumpStart=play?6.4:6.8;double jump=Jump(t,jumpStart,.95);
        if(play)jump=Math.Max(jump,Jump(t,10.4,1.1));
        if(feed||settle)jump=0;
        bool anticipation=(t>=jumpStart-.45&&t<jumpStart)||(play&&t>=9.95&&t<10.4);
        int pose=settle&&t>=8?3:jump>0?2:anticipation?1:feed&&t>=6?(referenceHandY>.85?1:0):approach<1&&t>=3.8?(int)(t*3)%2:0;
        bool requested=frame.Bank=="activity"&&frame.Frame==6&&frame.PropAlpha>0&&t>=6&&t<=14&&jump==0&&!anticipation;
        // A low reaching hand meets a crouching animal. A high hand gets a brief
        // greeting jump; the animal returns to ground while the person holds a gesture.
        if(requested&&referenceHandY>.85&&pose!=3)pose=1;
        if(settle&&pose==3)requested=false;
        double blend=requested?Smooth(Math.Min((t-6)/.8,(14-t)/.8)):0;
        return new(pose,offset,jump,requested,blend);
    }
    public static int SelectContactPose(int authoredPose,bool contactRequested,double handY,double uprightHeadY,double loweredHeadY)
    {
        if(authoredPose is <0 or >3||!double.IsFinite(handY)||!double.IsFinite(uprightHeadY)||!double.IsFinite(loweredHeadY))throw new ArgumentOutOfRangeException(nameof(authoredPose));
        // The same species can have a different crown height from another pet.
        // Choose an existing grounded drawing, never scale an animal or move
        // the companion to manufacture contact. Anticipation/leap/rest stay owned
        // by the motion timeline and cannot be replaced by this contact selector.
        if(!contactRequested||authoredPose is 2 or 3)return authoredPose;
        return Math.Abs(handY-uprightHeadY)<=Math.Abs(handY-loweredHeadY)?0:1;
    }
    private static double Jump(double time,double start,double duration)
    {double p=(time-start)/duration;return p is >0 and <1?4*p*(1-p)*.14:0;}
    private static double Smooth(double value){value=Math.Clamp(value,0,1);return value*value*(3-2*value);}
}
