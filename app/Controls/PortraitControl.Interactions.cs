using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

public partial class PortraitControl
{
    private InteractionDrawingElement? interactionDrawing;
    private CompanionInteractionScene? interactionScene;
    private CompanionPerformanceFrame? interactionFrame;
    private bool performanceFacingLeft;
    private object? reviewEvidence;
    private void EnsureInteractionLayer()
    {
        if(interactionDrawing!=null)return;
        interactionDrawing=new(){IsHitTestVisible=false,Visibility=Visibility.Collapsed,ClipToBounds=true};
        Grid.SetRow(interactionDrawing,0);Panel.SetZIndex(interactionDrawing,3);Stage.Children.Add(interactionDrawing);
        RenderOptions.SetBitmapScalingMode(interactionDrawing,BitmapScalingMode.NearestNeighbor);
        SizeChanged+=(_,_)=>{if(interactionFrame!=null&&performance?.IsPerforming==true)DrawPerformanceFrame(interactionFrame);};
    }
    private void HideInteraction()
    {
        interactionFrame=null;interactionScene=null;
        if(interactionDrawing!=null){interactionDrawing.Visibility=Visibility.Collapsed;interactionDrawing.Clear();}
        Portrait.Visibility=Visibility.Visible;
    }
    private void DrawPerformanceFrame(CompanionPerformanceFrame pose)
    {
        if(performance==null)return;
        if(TryDrawSylvanasAction(pose))return;
        var bank=pose.Bank=="pc"?pcFrames:pose.Bank=="motion"?motionFrames:pose.Bank=="activity"?activityFrames:frames;
        if(bank.Length==0){HideInteraction();return;}
        var source=bank[Math.Clamp(pose.Frame,0,bank.Length-1)];
        var prop=pose.PropIndex>=0?performance.Props[pose.PropIndex]:null;

        var rig=CompanionInteractionAssets.Rig(characterId,pose.Bank,pose.Frame);
        var reference=CompanionInteractionAssets.ReferenceRig(characterId,prop,pose);
        double side=Math.Min(PropLayer.ActualWidth,PropLayer.ActualHeight);
        // Use the SAME fitted canvas size as idle. It never varies with the pose or clip.
        double actorSide=side>0?Math.Clamp(Math.Min(Portrait.ActualWidth,Portrait.ActualHeight)/side,.1,1):1;
        bool isPet=prop?.Category is "pet" or "fantasy-pet";
        int propArtFrame=isPet?CompanionInteractionAssets.PetArtFrame(pose,reference,actorSide,prop):pose.PropFrame;
        var propSource=prop==null?null:CompanionInteractionAssets.Prop(prop,propArtFrame);
        var petHead=prop?.Category is "pet" or "fantasy-pet"?CompanionInteractionAssets.PetHead(propSource,propArtFrame):(InteractionPoint?)null;
        var refPetHead=petHead.HasValue?CompanionInteractionAssets.PetHead(CompanionInteractionAssets.Prop(prop!,0),0):(InteractionPoint?)null;
        interactionScene=CompanionInteractionLayout.Compose(pose,prop,rig,reference,actorSide,petHead,refPetHead,propArtFrame);
        if(pose.ClipId=="furniture.couch-tv")interactionScene=interactionScene with{AuthoredCouch=CompanionCouchArt.Available};
        interactionFrame=pose;
        EnsureInteractionLayer();interactionDrawing!.Set(interactionScene,source,propSource,performanceFacingLeft);
        interactionDrawing.Visibility=Visibility.Visible;Portrait.Visibility=Visibility.Hidden;PropImage.Visibility=Visibility.Collapsed;
        shownFrame=-1;timer.Interval=TimeSpan.FromMilliseconds(33);
        var b=SpriteAtlas.VisibleBounds(source);
        var visible=new InteractionRect(interactionScene.ActorCanvas.X+b.X/256*actorSide,interactionScene.ActorCanvas.Y+b.Y/256*actorSide,b.Width/256*actorSide,b.Height/256*actorSide);
        var foot=interactionScene.ActorCanvas.Map(rig.Feet);
        reviewEvidence=new{clipId=pose.ClipId,bank=pose.Bank,frame=pose.Frame,stageWidth=PropLayer.ActualWidth,stageHeight=PropLayer.ActualHeight,
            actorCanvas=interactionScene.ActorCanvas,actorScale=1d,actorRotation=0d,actorVisible=visible,foot,nearHand=interactionScene.NearHand,head=interactionScene.Head,
            propArtFrame,petCanvas=interactionScene.PropCanvas,contactTarget=interactionScene.ContactTarget,contactKind=interactionScene.ContactKind.ToString(),contactActive=interactionScene.ContactActive,
            contactError=interactionScene.ContactError,contactRequested=interactionScene.ContactRequested,realElapsedSeconds=pose.RealElapsedSeconds,anchorsVerified=interactionScene.ContactsVerified,facingLeft=performanceFacingLeft,
            handContactTarget=interactionScene.HandContactTarget,handContactError=interactionScene.HandContactError,available=true,
            bodyClipped=visible.X<-.001||visible.Y<-.001||visible.X+visible.Width>1.001||visible.Bottom>1.001,
            layers=interactionScene.ContactKind is CompanionContactKind.Seated or CompanionContactKind.Sleep?new[]{"furniture-back","actor","furniture-front"}:new[]{"actor","object","source-hand-contact"},
            authoredBedArtwork=CompanionSceneArt.HasBed,authoredPcArtwork=CompanionSceneArt.HasPc,pcBackView=pose.Bank=="pc",pcScreenFrame=pose.Bank=="pc"?CompanionPcMotion.Sample(pose.RealElapsedSeconds).ScreenFrame:(int?)null,
            authoredCouchArtwork=interactionScene.AuthoredCouch&&CompanionCouchArt.Available,couchScreenFrame=interactionScene.AuthoredCouch?CompanionCouchArt.ScreenFrame(pose.RealElapsedSeconds):(int?)null,
            coveredSleepingBodyMasked=interactionScene.ContactKind==CompanionContactKind.Sleep&&interactionScene.ContactActive&&CompanionSceneArt.HasBed,blanketOverBody=interactionScene.ContactKind==CompanionContactKind.Sleep&&interactionScene.ContactActive,
            petFixedCanvas=prop?.Category is "pet" or "fantasy-pet",note="Algebraic contact against estimated sprite anchors. Visual contact, anatomy and art quality remain subject to review."};
    }
    internal object ReviewGeometryEvidence()=>reviewEvidence??new{actorScale=1d,actorRotation=0d,anchorsVerified=false,contactActive=false,note="Neutral or no interaction frame."};
    private sealed class InteractionDrawingElement:FrameworkElement
    {
        private CompanionInteractionScene? scene;private BitmapSource? actor,prop;private bool facingLeft;
        public void Set(CompanionInteractionScene s,BitmapSource a,BitmapSource? p,bool mirror)
        {scene=s;actor=a;prop=p;facingLeft=mirror;InvalidateVisual();}
        public void Clear(){scene=null;actor=prop=null;InvalidateVisual();}
        protected override void OnRender(DrawingContext dc)
        {base.OnRender(dc);if(scene!=null&&actor!=null)CompanionInteractionDrawing.Draw(dc,scene,actor,prop,ActualWidth,ActualHeight,facingLeft);}
    }
}
