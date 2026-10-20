using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

public partial class PortraitControl
{
    private CompanionWeaponAnimation? weaponAnimation;
    private BitmapSource[] weaponFrames=[];
    private Canvas? weaponFxLayer;
    private WeaponEffectsElement? weaponEffects;
    public bool IsWeaponPerforming=>weaponAnimation?.IsPerforming==true;
    public bool HasWeaponArtwork=>weaponFrames.Length==12;
    public IReadOnlyList<string> WeaponAnimationIds=>HasWeaponArtwork?weaponAnimation?.Definitions.Select(d=>d.Id).ToArray()??[]:[];
    private void InitializeWeapons(string id)
    {
        StopWeapon();weaponAnimation=new(id);
        weaponFrames=weaponAnimation.Definitions.Count==0?[]:SpriteAtlas.Character(id,"weapons");
        if(weaponFrames.Length!=12)weaponFrames=[];
        EnsureWeaponEffectsLayer();
    }
    public void PlayWeapon(string id)
    {
        if(!HasWeaponArtwork||weaponAnimation==null||state is "speaking" or "listening" or "thinking" or "reading" or "quiet" or "paused")return;
        if(!weaponAnimation.Definitions.Any(d=>d.Id==id))return;
        StopPerformance();performanceFacingLeft=facingLeft;
        if(!weaponAnimation.Start(id,clock.Elapsed.TotalSeconds*AnimationSpeed))return;
        shownFrame=-1;UpdatePose();UpdateTimer();
    }
    private void StopWeapon()
    {
        weaponAnimation?.Stop();weaponEffects?.Clear();
        if(weaponFxLayer!=null)weaponFxLayer.Visibility=Visibility.Collapsed;
        Portrait.Source=frames.Length>0?frames[0]:null;Fallback.Visibility=frames.Length>0?Visibility.Collapsed:Visibility.Visible;
        Breath.ScaleX=Breath.ScaleY=1;Tilt.Angle=0;BodyMotion.X=BodyMotion.Y=FrameAlignment.X=FrameAlignment.Y=0;
        shownFrame=-1;
    }
    private bool TryUpdateWeapon(double virtualSeconds)
    {
        if(!IsWeaponPerforming||weaponAnimation==null)return false;
        if(!HasWeaponArtwork||state is "speaking" or "listening" or "thinking" or "reading" or "quiet" or "paused")
        {StopWeapon();return false;}
        var pose=weaponAnimation.Sample(virtualSeconds);
        if(pose.Done){StopWeapon();nextPerformance=clock.Elapsed.TotalSeconds+8+Random.Shared.NextDouble()*15;return false;}
        HideInteraction();Portrait.Visibility=Visibility.Visible;
        Portrait.Source=weaponFrames[pose.Frame];Fallback.Visibility=Visibility.Collapsed;shownFrame=-1;
        Portrait.RenderTransformOrigin=new Point(.5,.5);
        double side=Math.Max(1,Math.Min(PropLayer.ActualWidth,PropLayer.ActualHeight));
        double direction=performanceFacingLeft?-1:1;
        Breath.ScaleX=direction;Breath.ScaleY=1;Tilt.Angle=0;
        BodyMotion.X=BodyMotion.Y=FrameAlignment.X=FrameAlignment.Y=0;AlignFeetToFloor();
        reviewEvidence=new{clipId=pose.ClipId,bank="weapons",frame=pose.Frame,actorScale=1d,actorRotation=0d,facingLeft=performanceFacingLeft,anchorsVerified=false,contactActive=false,note="Fixed-size registered weapon pose; signature FX are clipped to this avatar."};
        PropImage.Visibility=Visibility.Collapsed;
        EnsureWeaponEffectsLayer();
        weaponFxLayer!.RenderTransformOrigin=new Point(.5,.5);
        weaponFxLayer.RenderTransform=new ScaleTransform(direction,1);
        weaponEffects!.Width=Math.Max(1,PropLayer.ActualWidth);weaponEffects.Height=Math.Max(1,PropLayer.ActualHeight);
        weaponEffects.SetFrame(pose);weaponFxLayer!.Visibility=pose.EffectAlpha>0?Visibility.Visible:Visibility.Collapsed;
        timer.Interval=TimeSpan.FromMilliseconds(33);return true;
    }
    private void EnsureWeaponEffectsLayer()
    {
        if(weaponFxLayer!=null)return;
        weaponEffects=new(){IsHitTestVisible=false};
        weaponFxLayer=new(){IsHitTestVisible=false,ClipToBounds=true,Visibility=Visibility.Collapsed,Background=Brushes.Transparent};
        weaponFxLayer.Children.Add(weaponEffects);Grid.SetRow(weaponFxLayer,0);Panel.SetZIndex(weaponFxLayer,10);Stage.Children.Add(weaponFxLayer);
        Unloaded+=(_,_)=>StopWeapon();
    }
    // A single drawing element replaces many per-tick Shape objects. All marks are clipped to
    // the avatar's existing window, use transparent backgrounds, and never create audio or input.
    private sealed class WeaponEffectsElement:FrameworkElement
    {
        private CompanionWeaponFrame? current;
        private static readonly Brush Lightning=FrozenBrush(Color.FromRgb(85,221,220)),Shadow=FrozenBrush(Color.FromRgb(173,108,223)),
            Frost=FrozenBrush(Color.FromRgb(140,225,255)),Hextech=FrozenBrush(Color.FromRgb(76,207,247)),Burst=FrozenBrush(Color.FromRgb(248,143,193)),White=FrozenBrush(Color.FromRgb(244,246,255));
        public void SetFrame(CompanionWeaponFrame pose){current=pose;InvalidateVisual();}
        public void Clear(){current=null;InvalidateVisual();}
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);var pose=current;if(pose==null||pose.EffectAlpha<=0||ActualWidth<=0||ActualHeight<=0)return;
            double side=Math.Min(ActualWidth,ActualHeight),left=(ActualWidth-side)/2,top=(ActualHeight-side)/2;
            Point P(double x,double y)=>new(left+Math.Clamp(x,0,1)*side,top+Math.Clamp(y,0,1)*side);
            var origin=P(pose.OriginX,pose.OriginY);var target=P(pose.TargetX,pose.TargetY);
            Brush color=pose.Effect switch{CompanionWeaponEffect.Lightning=>Lightning,CompanionWeaponEffect.ShadowArrow=>Shadow,
                CompanionWeaponEffect.Frost=>Frost,CompanionWeaponEffect.Hextech=>Hextech,_=>Burst};
            var pen=new Pen(color,Math.Clamp(side*.011,.7,3));pen.Freeze();var bright=new Pen(White,Math.Clamp(side*.005,.5,1.5));bright.Freeze();
            dc.PushClip(new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight)));dc.PushOpacity(Math.Clamp(pose.EffectAlpha,0,1));
            switch(pose.Effect)
            {
                case CompanionWeaponEffect.Lightning:
                    for(int branch=0;branch<3;branch++)
                    {
                        Point previous=origin;for(int i=1;i<=6;i++)
                        {double t=i/6.0;double wiggle=Math.Sin(i*2.7+pose.EffectPhase*17+branch)*side*.025*Math.Sin(t*Math.PI);var next=new Point(origin.X+(target.X-origin.X)*t+wiggle,origin.Y+(target.Y-origin.Y)*t+(branch-1)*side*.035*t);dc.DrawLine(branch==1?bright:pen,previous,next);previous=next;}
                    }
                    Ring(dc,origin,pose.Radius*side,pen,pose.Pulse);Sparks(dc,origin,pose.Radius*side,pen,pose.EffectPhase,7);break;
                case CompanionWeaponEffect.ShadowArrow:
                    Projectile(dc,origin,target,pose.EffectPhase,pen,bright,side);Ring(dc,origin,pose.Radius*side,pen,pose.Pulse);break;
                case CompanionWeaponEffect.Frost:
                    Ring(dc,origin,pose.Radius*side,pen,pose.Pulse);Ring(dc,origin,pose.Radius*side*.64,bright,1-pose.Pulse);
                    for(int i=0;i<6;i++){double a=i*Math.PI/3+pose.EffectPhase*.8;var end=new Point(origin.X+Math.Cos(a)*pose.Radius*side,origin.Y+Math.Sin(a)*pose.Radius*side);dc.DrawLine(pen,origin,end);var cross=new Vector(Math.Cos(a+Math.PI/2),Math.Sin(a+Math.PI/2))*side*.018;var near=origin+(end-origin)*.72;dc.DrawLine(bright,near-cross,near+cross);}break;
                case CompanionWeaponEffect.Hextech:
                    Ring(dc,origin,pose.Radius*side,pen,pose.Pulse);Ring(dc,target,pose.Radius*side*.45,bright,1-pose.Pulse);Sparks(dc,target,pose.Radius*side*.62,pen,pose.EffectPhase,5);dc.DrawLine(bright,origin,target);break;
                case CompanionWeaponEffect.CartoonBurst:
                    Projectile(dc,origin,target,(pose.EffectPhase*3)%1,pen,bright,side);Sparks(dc,origin,pose.Radius*side,pen,pose.EffectPhase,6);Ring(dc,target,pose.Radius*side*.4,pen,pose.Pulse);break;
            }
            dc.Pop();dc.Pop();
        }
        private static void Ring(DrawingContext dc,Point center,double radius,Pen pen,double pulse)=>dc.DrawEllipse(null,pen,center,radius*(.65+.35*pulse),radius*(.65+.35*pulse));
        private static void Sparks(DrawingContext dc,Point center,double radius,Pen pen,double phase,int count)
        {for(int i=0;i<count;i++){double angle=i*Math.PI*2/count+phase*2;var vector=new Vector(Math.Cos(angle),Math.Sin(angle));dc.DrawLine(pen,center+vector*radius*.7,center+vector*radius);}}
        private static void Projectile(DrawingContext dc,Point origin,Point target,double phase,Pen pen,Pen bright,double side)
        {
            phase=Math.Clamp(phase,0,1);var direction=target-origin;var head=origin+direction*phase;var tail=origin+direction*Math.Max(0,phase-.24);dc.DrawLine(pen,tail,head);dc.DrawEllipse(White,bright,head,side*.012,side*.012);
            if(direction.Length>0){direction.Normalize();var cross=new Vector(-direction.Y,direction.X);dc.DrawLine(bright,head,head-direction*side*.035+cross*side*.015);dc.DrawLine(bright,head,head-direction*side*.035-cross*side*.015);}
        }
        private static Brush FrozenBrush(Color color){var brush=new SolidColorBrush(color);brush.Freeze();return brush;}
    }
}
