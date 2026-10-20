using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

/// <summary>Measured source-art crops only. The opaque TV content is confined to
/// its four-corner screen aperture; sofa parts retain alpha and a single fixed scale.</summary>
internal static class CompanionCouchArt
{
    private sealed record Spec(string LayerStem,string LayerSha256,string ScreenStem,string ScreenSha256,
        int[] CouchCrop,int[] FrontCrop,double[] FrontPlacement,int[] TvCrop,int[][] ScreenRects,double[][] ScreenQuad,
        double SeatX,double SeatY,double FloorY);
    private static Spec? spec;
    private static bool loaded;
    private static BitmapSource? couch,front,tv;
    private static BitmapSource[] screens=[];
    internal static bool Available{get{Load();return couch!=null&&front!=null&&tv!=null&&screens.Length==4;}}
    private static BitmapSource? Open(string stem,string sha)
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Assets",stem+".png");
        if(!File.Exists(path)||!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),sha,StringComparison.OrdinalIgnoreCase))return null;
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();return image;
    }
    private static BitmapSource? Crop(BitmapSource source,int[] r)
    {
        if(r.Length!=4||r[0]<0||r[1]<0||r[2]<=0||r[3]<=0||r[0]+r[2]>source.PixelWidth||r[1]+r[3]>source.PixelHeight)return null;
        var image=new CroppedBitmap(source,new(r[0],r[1],r[2],r[3]));image.Freeze();return image;
    }
    private static void Load()
    {
        if(loaded)return;loaded=true;
        var path=Path.Combine(AppContext.BaseDirectory,"Assets","sylvanas-couch-registration.json");if(!File.Exists(path))return;
        spec=JsonSerializer.Deserialize<Spec>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
        if(spec==null||spec.ScreenRects.Length!=4||spec.ScreenQuad.Length!=4||spec.ScreenQuad.Any(p=>p.Length!=2)||spec.FrontPlacement.Length!=2||spec.FloorY<=spec.SeatY)return;
        var layer=Open(spec.LayerStem,spec.LayerSha256);var content=Open(spec.ScreenStem,spec.ScreenSha256);if(layer==null||content==null)return;
        // Reject the earlier opaque-gradient furniture attempt instead of showing a box.
        var rgba=new FormatConvertedBitmap(layer,PixelFormats.Bgra32,null,0);var pixel=new byte[4];rgba.CopyPixels(new(0,0,1,1),pixel,4,0);if(pixel[3]!=0)return;
        couch=Crop(layer,spec.CouchCrop);front=Crop(layer,spec.FrontCrop);tv=Crop(layer,spec.TvCrop);
        var frames=spec.ScreenRects.Select(r=>Crop(content,r)).ToArray();if(frames.All(f=>f!=null))screens=frames.Select(f=>f!).ToArray();
    }
    internal static int ScreenFrame(double realSeconds)=>2+(int)(Math.Max(0,realSeconds)/.8)%4;
    internal static void DrawBack(DrawingContext dc,CompanionInteractionScene scene)
    {
        if(!Available)return;var s=spec!;double scale=(1-scene.Seat.Y)/(s.FloorY-s.SeatY),xScale=Math.Min(scale,.92/s.CouchCrop[2]);
        // Seat and floor heights follow each unchanged actor's seated legs.
        // Bound furniture width independently so taller seats cannot crop its arms.
        dc.DrawImage(couch,new Rect(scene.Seat.X+(s.CouchCrop[0]-s.SeatX)*xScale,1+(s.CouchCrop[1]-s.FloorY)*scale,s.CouchCrop[2]*xScale,s.CouchCrop[3]*scale));
        // The TV stands in front of the sofa's otherwise empty right side. It has
        // a fixed size independent of the person's pose and never covers the face.
        double tvScale=.285/s.TvCrop[2],x=.71,y=1-s.TvCrop[3]*tvScale;
        dc.DrawImage(tv,new Rect(x,y,.285,s.TvCrop[3]*tvScale));
        var q=s.ScreenQuad.Select(p=>new Point(x+(p[0]-s.TvCrop[0])*tvScale,y+(p[1]-s.TvCrop[1])*tvScale)).ToArray();
        var picture=screens[ScreenFrame(scene.RealElapsedSeconds)-2];
        // Two source-preserving affine triangles map the image into the oblique
        // four-corner aperture. No pixels outside the monitor receive RGB art.
        DrawTriangle(dc,picture,q[0],q[1],q[2],q[1]-q[0],q[2]-q[1]);
        DrawTriangle(dc,picture,q[0],q[2],q[3],q[2]-q[3],q[3]-q[0]);
    }
    internal static void DrawFront(DrawingContext dc,CompanionInteractionScene scene)
    {
        if(!Available)return;var s=spec!;double scale=(1-scene.Seat.Y)/(s.FloorY-s.SeatY),xScale=Math.Min(scale,.92/s.CouchCrop[2]);
        dc.DrawImage(front,new Rect(scene.Seat.X+(s.FrontPlacement[0]-s.SeatX)*xScale,1+(s.FrontPlacement[1]-s.FloorY)*scale,s.FrontCrop[2]*xScale,s.FrontCrop[3]*scale));
    }
    private static void DrawTriangle(DrawingContext dc,BitmapSource source,Point a,Point b,Point c,Vector u,Vector v)
    {
        var geometry=new StreamGeometry();using(var g=geometry.Open()){g.BeginFigure(a,true,true);g.LineTo(b,true,false);g.LineTo(c,true,false);}geometry.Freeze();
        dc.PushClip(geometry);dc.PushTransform(new MatrixTransform(u.X,u.Y,v.X,v.Y,a.X,a.Y));
        dc.DrawImage(source,new Rect(0,0,1,1));dc.Pop();dc.Pop();
    }
}
