using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

/// <summary>One opaque quilt with a moving folded edge. Breathing never fades cloth.
/// The uncovered head and upper clothed chest share the same boundary as the quilt.
/// This is runtime composition; approved sprite and furniture source files are unchanged.</summary>
internal static class CompanionBedDrawing
{
    internal static double FoldEdge(CompanionInteractionScene scene)
    {
        double head=scene.HeadOnLeft?scene.Head.X:1-scene.Head.X;
        double rest=Math.Clamp(head+.29*scene.ActorCanvas.Width,.37,.48);
        if(!scene.ContactActive)return .69;
        double t=scene.RealElapsedSeconds;
        if(t<6.5)return .69+(rest-.69)*Smooth((t-5)/1.5);
        if(t>18.5)return rest+(.69-rest)*Smooth((t-18.5)/2);
        return rest;
    }
    internal static double BreathLift(double seconds)=>seconds<6.5||seconds>18.5?0:
        .004*(1-Math.Cos((seconds-6.5)*Math.PI*2/4.8));

    internal static void DrawExposedActor(DrawingContext dc,CompanionInteractionScene scene,BitmapSource actor)
    {
        var cloth=ClothRegion(scene);
        var exposed=new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(new Rect(0,0,1,1)),cloth);
        dc.PushClip(exposed);
        var r=scene.ActorCanvas;dc.DrawImage(actor,new Rect(r.X,r.Y,r.Width,r.Height));
        dc.Pop();
    }
    internal static void DrawBlanket(DrawingContext dc,CompanionInteractionScene scene)
    {
        if(!CompanionSceneArt.HasBed)return;
        dc.PushClip(ClothRegion(scene));
        if(!scene.HeadOnLeft)dc.PushTransform(new MatrixTransform(-1,0,0,1,1,0));
        // The generated quilt contains the footpost. Keep that rigid landmark still;
        // only the cloth portion rises a fraction of a source pixel with each breath.
        const double post=.905;
        dc.PushClip(new RectangleGeometry(new Rect(0,0,post,1)));
        double lift=BreathLift(scene.RealElapsedSeconds);
        dc.DrawImage(CompanionSceneArt.Bed[1],new Rect(0,-lift,1,1+lift));
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(post,0,1-post,1)));
        dc.DrawImage(CompanionSceneArt.Bed[1],new Rect(0,0,1,1));
        dc.Pop();
        if(!scene.HeadOnLeft)dc.Pop();
        dc.Pop();
    }
    private static Geometry ClothRegion(CompanionInteractionScene scene)
    {
        double edge=FoldEdge(scene);
        // A shallow sloped fold, away from the face and neckline, instead of a box
        // over the head. Its boundary is shared by body occlusion and cloth drawing.
        Point[] points=[new(edge+.018,0),new(1,0),new(1,1),new(edge-.026,1),new(edge-.01,.78),new(edge+.018,.65)];
        var g=new StreamGeometry();using(var c=g.Open())
        {c.BeginFigure(points[0],true,true);c.PolyLineTo(points.Skip(1).ToArray(),true,false);}
        if(!scene.HeadOnLeft)g.Transform=new MatrixTransform(-1,0,0,1,1,0);
        g.Freeze();return g;
    }
    private static double Smooth(double value){value=Math.Clamp(value,0,1);return value*value*(3-2*value);}
}
