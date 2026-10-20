using System.Windows;

namespace AzerothCompanion.Controls;

public partial class PortraitControl
{
    private bool TryDrawSylvanasAction(CompanionPerformanceFrame pose)
    {
        if(characterId!="sylvanas"||pose.Bank is not (CompanionSylvanasActions.StepsBank or CompanionSylvanasActions.HandlingBank))return false;
        var bank=SpriteAtlas.Character(characterId,pose.Bank);if(bank.Length!=12){RenderReviewNeutral();return true;}
        HideInteraction();Portrait.Visibility=Visibility.Visible;PropImage.Visibility=Visibility.Collapsed;
        Portrait.Source=bank[pose.Frame];Fallback.Visibility=Visibility.Collapsed;shownFrame=-1;
        Breath.ScaleX=performanceFacingLeft?-1:1;Breath.ScaleY=1;Tilt.Angle=0;
        BodyMotion.X=BodyMotion.Y=FrameAlignment.X=FrameAlignment.Y=0;AlignFeetToFloor();
        var source=bank[pose.Frame];var b=SpriteAtlas.VisibleBounds(source);
        double fitted=Math.Min(Portrait.ActualWidth/source.PixelWidth,Portrait.ActualHeight/source.PixelHeight);
        var bounds=Portrait.TransformToVisual(PropLayer).TransformBounds(new Rect((Portrait.ActualWidth-source.PixelWidth*fitted)/2+b.X*fitted,(Portrait.ActualHeight-source.PixelHeight*fitted)/2+b.Y*fitted,b.Width*fitted,b.Height*fitted));
        reviewEvidence=new{clipId=pose.ClipId,bank=pose.Bank,frame=pose.Frame,actorScale=1d,actorRotation=0d,available=true,
            stageWidth=PropLayer.ActualWidth,stageHeight=PropLayer.ActualHeight,actorVisiblePixels=bounds,
            bodyClipped=bounds.Left<-.01||bounds.Top<-.01||bounds.Right>PropLayer.ActualWidth+.01||bounds.Bottom>PropLayer.ActualHeight+.51,
            soleGapPixels=PropLayer.ActualHeight-bounds.Bottom,realElapsedSeconds=pose.RealElapsedSeconds,facingLeft=performanceFacingLeft,
            genericPropOverlay=false,equipmentInAuthoredPose=pose.Bank==CompanionSylvanasActions.HandlingBank,
            anchorsVerified=false,contactActive=false,visualContactApproved=false,
            note="One fixed source scale; authored complete body/equipment poses. Numeric bounds and source contact are separate from human animation approval."};
        timer.Interval=TimeSpan.FromMilliseconds(33);return true;
    }
}
