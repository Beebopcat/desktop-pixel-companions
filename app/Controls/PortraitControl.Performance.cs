using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AzerothCompanion.Core;

namespace AzerothCompanion.Controls;

public partial class PortraitControl
{
    private CompanionPerformance? performance;
    private readonly CompanionGaitClock gait=new();
    private BitmapSource[] motionFrames=[], activityFrames=[], pcFrames=[];
    private double nextPerformance;
    public bool IsPerforming => performance?.IsPerforming == true || IsWeaponPerforming;
    public IReadOnlyList<string> AnimationIds => (performance?.Definitions.Select(d=>d.Id) ?? []).Concat(WeaponAnimationIds).ToArray();
    public int PropCount => performance?.Props.Count ?? 0;
    public IReadOnlyList<CompanionAnimationAvailability> AnimationAvailability => AnimationIds.Select(id=>new CompanionAnimationAvailability(id,IsAnimationAvailable(id),AnimationUnavailableReason(id))).ToArray();
    public bool IsAnimationAvailable(string id)=>AnimationUnavailableReason(id)==null;
    public string? AnimationUnavailableReason(string id)
    {
        id=id switch{"wave"=>"gesture.wave","duo-wave"=>"duo.wave","high-five"=>"duo.high-five",_=>id};
        if(id.StartsWith("weapon.",StringComparison.Ordinal))return WeaponAnimationIds.Contains(id)?null:"Signature weapon artwork is unavailable for this character.";
        if(performance?.Definitions.Any(d=>d.Id==id)!=true)return "Unknown animation.";
        var repairBank=CompanionSylvanasActions.RequiredBank(characterId,id);
        if(repairBank!=null&&SpriteAtlas.Character(characterId,repairBank).Length!=12)return "This reported Sylvanas scene is awaiting verified authored repair artwork; the rejected generic animation is withheld.";
        if(id=="furniture.couch-tv"&&!CompanionCouchArt.Available)return "Couch/TV artwork is unavailable; the rejected placeholder is withheld.";
        if(id=="furniture.pc")
        {
            if(pcFrames.Length!=12)return "Rear-view PC poses are unavailable for this character.";
            if(!CompanionSceneArt.HasPc)return "Registered PC desk and game-screen artwork is unavailable.";
        }
        if(id=="furniture.bed"&&!CompanionSceneArt.HasBed)return "Authored mattress and blanket artwork is unavailable.";
        return null;
    }

    private void InitializePerformance(string id)
    {
        Portrait.RenderTransformOrigin=new Point(.5,.5);
        var basic=SpriteAtlas.Character(id,"basic");
        if(basic.Length==12){frames=basic;pixelSheet=true;Fallback.Visibility=Visibility.Collapsed;}
        motionFrames=SpriteAtlas.Character(id,"motion");activityFrames=SpriteAtlas.Character(id,"activity");pcFrames=SpriteAtlas.Character(id,"pc");
        performance=new CompanionPerformance(id);
        gait.Reset();
        HideInteraction();EnsureInteractionLayer();
        nextPerformance=clock.Elapsed.TotalSeconds+2+Random.Shared.NextDouble()*3;
        PropImage.Visibility=Visibility.Collapsed;
    }
    public void PlayAnimation(string id)
    {
        if(FunctionalTestMode.Active)return;
        if(performance==null||state is "speaking" or "listening" or "thinking" or "reading" or "quiet" or "paused")return;
        if(!IsAnimationAvailable(id))return;
        if(id.StartsWith("weapon.",StringComparison.Ordinal)){performance.Stop();PlayWeapon(id);UpdatePose();UpdateTimer();return;}
        if(!performance.Start(id,clock.Elapsed.TotalSeconds*AnimationSpeed))return;
        StopWeapon();performanceFacingLeft=facingLeft;shownFrame=-1;UpdatePose();UpdateTimer();
    }
    public void SummonRandomProp()
    {
        if(FunctionalTestMode.Active)return;
        if(performance==null||state is "speaking" or "listening" or "thinking" or "reading" or "quiet" or "paused")return;
        StopWeapon();performanceFacingLeft=facingLeft;StartRandomAvailableProp(clock.Elapsed.TotalSeconds*AnimationSpeed);shownFrame=-1;UpdatePose();UpdateTimer();
    }
    private bool StartRandomAvailableProp(double virtualSeconds)
    {
        if(performance==null)return false;
        for(int i=0;i<performance.Props.Count;i++)
        {
            int prop=performance.NextPropIndex();string id=performance.GetDefaultClipId(prop);
            if(IsAnimationAvailable(id))return performance.Start(id,virtualSeconds);
        }
        return false;
    }
    private void StopPerformance()
    {
        performance?.Stop();StopWeapon();HideInteraction();PropImage.Visibility=Visibility.Collapsed;shownFrame=-1;
        nextPerformance=clock.Elapsed.TotalSeconds+3+Random.Shared.NextDouble()*3;
    }
    private bool UpdatePerformance(double virtualSeconds)
    {
        if(performance==null)return false;
        if(!walking&&state=="idle"&&!IsPerforming&&clock.Elapsed.TotalSeconds>=nextPerformance)
        {
            performanceFacingLeft=facingLeft;
            if(HasWeaponArtwork&&Random.Shared.Next(6)==0)
            {PlayWeapon(WeaponAnimationIds[Random.Shared.Next(WeaponAnimationIds.Count)]);return true;}
            if(Random.Shared.Next(4)==0)
            {
                var solo=performance.Definitions.Where(d=>!d.RequiresPartner&&IsAnimationAvailable(d.Id)).ToArray();
                if(solo.Length>0)performance.Start(solo[Random.Shared.Next(solo.Length)].Id,virtualSeconds);
            }
            else StartRandomAvailableProp(virtualSeconds);
        }
        if(IsPerforming)
        {
            var pose=performance.Sample(virtualSeconds);
            if(pose.Done)
            {
                nextPerformance=clock.Elapsed.TotalSeconds+3+Random.Shared.NextDouble()*4;
                HideInteraction();PropImage.Visibility=Visibility.Collapsed;shownFrame=-1;return false;
            }
            DrawPerformanceFrame(pose);
            timer.Interval=TimeSpan.FromMilliseconds(33);return true;
        }
        HideInteraction();PropImage.Visibility=Visibility.Collapsed;
        if(walking&&motionFrames.Length>=8) // every state walks while roaming (speaking/reading used to slide frozen)
        {
            int frame=gait.Sample(virtualSeconds);Portrait.Source=motionFrames[frame];shownFrame=-1;
            Breath.ScaleX=facingLeft?-1:1;Breath.ScaleY=1;Tilt.Angle=0;
            BodyMotion.X=BodyMotion.Y=FrameAlignment.X=FrameAlignment.Y=0;
            AlignFeetToFloor();
            // Step bounce + slight sway so walk cycles with few distinct leg poses still read as walking.
            BodyMotion.Y=-2.2*Math.Abs(Math.Sin(Math.PI*frame/4.0));Tilt.Angle=1.2*Math.Sin(Math.PI*frame/4.0)*(facingLeft?-1:1);
            timer.Interval=TimeSpan.FromMilliseconds(20);return true;
        }
        gait.Reset();
        return false;
    }
    internal void RenderAnimationAt(string id,double virtualElapsed)
    {
        // Review uses an exact supplied clock and never starts a dispatcher timer.
        timer.Stop();performanceFacingLeft=facingLeft;
        if(!IsAnimationAvailable(id))
        {
            performance?.Stop();StopWeapon();RenderReviewNeutral();
            reviewEvidence=new{clipId=id,available=false,unavailableReason=AnimationUnavailableReason(id),actorScale=1d,actorRotation=0d,anchorsVerified=false,contactActive=false,neutral=true};
            return;
        }
        if(id.StartsWith("weapon.",StringComparison.Ordinal))
        {
            performance?.Stop();HideInteraction();
            weaponAnimation?.Start(id,0);
            if(!TryUpdateWeapon(virtualElapsed))RenderReviewNeutral();
            timer.Stop();return;
        }
        StopWeapon();performance?.Start(id,0);
        var pose=performance?.Sample(virtualElapsed);
        if(pose?.Performing==true)DrawPerformanceFrame(pose);
        else RenderReviewNeutral();
        timer.Stop();
    }
    internal void RenderWalkingFrameAt(int frame)
    {
        if(frame is <0 or >7||motionFrames.Length<8)throw new ArgumentOutOfRangeException(nameof(frame));
        timer.Stop();performance?.Stop();StopWeapon();HideInteraction();PropImage.Visibility=Visibility.Collapsed;
        Portrait.Source=motionFrames[frame];shownFrame=-1;
        Breath.ScaleX=facingLeft?-1:1;Breath.ScaleY=1;Tilt.Angle=0;
        BodyMotion.X=BodyMotion.Y=FrameAlignment.X=FrameAlignment.Y=0;AlignFeetToFloor();
    }
    private void RenderReviewNeutral()
    {
        HideInteraction();PropImage.Visibility=Visibility.Collapsed;Portrait.Source=frames.Length>0?frames[0]:null;
        Breath.ScaleX=facingLeft?-1:1;Breath.ScaleY=1;Tilt.Angle=0;
        BodyMotion.X=BodyMotion.Y=FrameAlignment.X=FrameAlignment.Y=0;shownFrame=0;AlignFeetToFloor();
        reviewEvidence=new{actorScale=1d,actorRotation=0d,anchorsVerified=false,contactActive=false,neutral=true,note="Deterministic neutral completion."};
    }
}
