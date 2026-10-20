using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

/// <summary>Measured immutable source portions from the shared PC scene. Chair seat and
/// wheel base are separate layers, so moving the seat does not resize an actor or lift wheels.
/// Display crops, keyboard and desktop share fixed anchors throughout an entire performance.</summary>
internal static class CompanionPcDrawing
{
    private static BitmapSource? mouse;
    /// <summary>The approved actors are viewed from behind. Their complete backs,
    /// near arms and hands are foreground; a frontal desk lip must not slice the
    /// abdomen and then be patched with generic circular hand copies.</summary>
    internal static void DrawScene(DrawingContext dc,CompanionInteractionScene scene,BitmapSource actor)
    {
        if(scene.Alpha>0)
        {
            dc.PushOpacity(scene.Alpha);
            DrawBack(dc,scene);
            DrawInputs(dc,scene);
            dc.Pop();
        }
        // Preserve every authored body/hand pixel and its immutable canvas.
        // The actual sprite occludes equipment naturally, with no cloned hands.
        dc.DrawImage(actor,new Rect(scene.ActorCanvas.X,scene.ActorCanvas.Y,
            scene.ActorCanvas.Width,scene.ActorCanvas.Height));
    }
    internal static (Rect Keyboard,Rect Mouse) Inputs(CompanionInteractionScene scene)
    {
        // TableHand is the fixed PC reference hand, rather than the current
        // animated wrist. Furniture and input devices do not chase each pose.
        double x=scene.TableHand.X,y=scene.TableHand.Y;
        return(new(x-.165,y+.008,.190,.036),new(x-.025,y+.002,.050,.032));
    }
    private static void DrawInputs(DrawingContext dc,CompanionInteractionScene scene)
    {
        var inputs=Inputs(scene);
        if(CompanionSceneArt.PcKeyboard!=null)dc.DrawImage(CompanionSceneArt.PcKeyboard,inputs.Keyboard);
        if(mouse==null&&CompanionSceneArt.HasPc)
        {
            // Read only the existing authored mouse; no generated replacement.
            var cells=SpriteAtlas.Load("shared-pc-scene-v2",3,2);
            if(cells.Length==6)mouse=new CroppedBitmap(cells[1],new Int32Rect(273,242,50,32));
            mouse?.Freeze();
        }
        if(mouse!=null)dc.DrawImage(mouse,inputs.Mouse);
    }
    private const double FurnitureScale=.87;
    private static readonly Geometry Chair=ChairOutline();
    private static readonly Geometry DeskWithoutChair=new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(new(0,0,1,1)),Chair);
    private static readonly Brush Post=new SolidColorBrush(Color.FromRgb(44,53,60));
    private static readonly Brush PostLight=new SolidColorBrush(Color.FromRgb(105,124,130));
    private static (double X,double Y) DeskOffset(CompanionInteractionScene scene)
        =>(.5-FurnitureScale*((45+511)/2.0/512),scene.TableHand.Y+.015-FurnitureScale*(254.0/512));
    internal static void DrawBack(DrawingContext dc,CompanionInteractionScene scene)
    {
        var (x,y)=DeskOffset(scene);
        dc.PushTransform(new MatrixTransform(FurnitureScale,0,0,FurnitureScale,x,y));
        dc.PushClip(DeskWithoutChair);dc.DrawImage(CompanionSceneArt.Pc[0],new(0,0,1,1));dc.Pop();
        var phase=CompanionPcMotion.Sample(scene.RealElapsedSeconds);
        if(phase.Playing)dc.DrawImage(CompanionSceneArt.Pc[phase.ScreenFrame],new(0,0,1,1));
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Intersect,Chair,new RectangleGeometry(new(0,410.0/512,1,1-410.0/512))));
        dc.DrawImage(CompanionSceneArt.Pc[0],new(0,0,1,1));dc.Pop();dc.Pop();

        double wheelPostY=y+FurnitureScale*(428.0/512);
        dc.DrawLine(new Pen(Post,.028),new(scene.Seat.X,scene.Seat.Y),new(scene.Seat.X,wheelPostY));
        dc.DrawLine(new Pen(PostLight,.006),new(scene.Seat.X-.007,scene.Seat.Y),new(scene.Seat.X-.007,wheelPostY));
        double chairX=scene.Seat.X-FurnitureScale*(189.0/512),chairY=scene.Seat.Y-FurnitureScale*(392.0/512);
        dc.PushTransform(new MatrixTransform(FurnitureScale,0,0,FurnitureScale,chairX,chairY));
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Intersect,Chair,new RectangleGeometry(new(0,0,1,414.0/512))));
        dc.DrawImage(CompanionSceneArt.Pc[0],new(0,0,1,1));dc.Pop();dc.Pop();
    }
    internal static void DrawFront(DrawingContext dc,CompanionInteractionScene scene)
    {
        // Retained for the legacy generic drawer's compile-time contract.
        // Active PC scenes dispatch through DrawScene and do not use this pass.
        var (x,y)=DeskOffset(scene);
        dc.PushTransform(new MatrixTransform(FurnitureScale,0,0,FurnitureScale,x,y));
        var oldKeyboard=new Rect((16+111*1.01)/512.0,243.0/512,160*1.01/512,30.0/512);
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(new(0,0,1,1)),new RectangleGeometry(oldKeyboard)));
        dc.DrawImage(CompanionSceneArt.Pc[1],new(0,0,1,1));dc.Pop();
        // Fill only the lip's small keyboard-occluded source portion with authored
        // desktop texture. This is a runtime crop, never a changed raster asset.
        if(CompanionSceneArt.PcWood!=null)dc.DrawImage(CompanionSceneArt.PcWood,new(oldKeyboard.X,254.0/512,oldKeyboard.Width,20.0/512));
        dc.Pop();
        if(CompanionSceneArt.PcKeyboard!=null)
        {
            const double width=.272,height=.051;
            dc.DrawImage(CompanionSceneArt.PcKeyboard,new(scene.TableHand.X-width*.68,scene.TableHand.Y-height*.75,width,height));
        }
    }
    private static Geometry ChairOutline()
    {
        (double X,double Y)[] points=[(135,215),(228,213),(248,238),(265,318),(260,351),(291,326),(292,353),(268,365),(261,399),(217,414),(211,423),(256,433),(272,445),(267,473),(246,476),(211,449),(201,480),(179,484),(171,450),(134,470),(107,474),(101,462),(106,444),(123,433),(169,417),(156,410),(124,404),(111,359),(87,353),(84,330),(96,324),(118,325),(127,274)];
        var geometry=new StreamGeometry();using(var g=geometry.Open())
        {g.BeginFigure(new(points[0].X/512,points[0].Y/512),true,true);g.PolyLineTo(points.Skip(1).Select(p=>new Point(p.X/512,p.Y/512)).ToArray(),true,false);}
        geometry.Freeze();return geometry;
    }
}
