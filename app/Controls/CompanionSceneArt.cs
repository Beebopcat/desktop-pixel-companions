using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Security.Cryptography;

namespace AzerothCompanion.Controls;

/// <summary>Immutable authored scene layers. Every cell uses one source-pixel scale and a
/// fixed footpost anchor; breathing never changes the mattress width or actor size.</summary>
internal static class CompanionSceneArt
{
    private static BitmapSource[]? bed;
    private static BitmapSource[]? pc;
    private const string PcSourceSha256="0CCF742F305F9703F1058D1CD1371EE65AF73DBE10735205E55088678FC8FBB5";
    internal static BitmapSource? PcKeyboard{get;private set;}
    internal static BitmapSource? PcWood{get;private set;}
    internal static bool HasBed=>Bed.Length==6;
    internal static bool HasPc=>Pc.Length==6;
    internal static BitmapSource[] Pc
    {
        get
        {
            if(pc!=null)return pc;
            var path=Path.Combine(AppContext.BaseDirectory,"Assets","shared-pc-scene-v2.png");
            if(!File.Exists(path)||Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=PcSourceSha256)return pc=[];
            var cells=SpriteAtlas.Load("shared-pc-scene-v2",3,2);
            // These layers share a source canvas. Never individually alpha-fit the
            // keyboard or screen: that would detach them from the desk registration.
            if(cells.Length!=6||cells.Any(cell=>cell.PixelWidth!=512||cell.PixelHeight!=512))return pc=[];
            PcKeyboard=Crop(cells[1],new(111,243,160,30));
            PcWood=Crop(cells[1],new(335,255,85,20));
            var result=new BitmapSource[6];result[0]=RegisterCanvas(cells[0]);result[1]=RegisterPcFront(cells[1]);
            Int32Rect[] sourceRects=[new(194,75,267,168),new(197,56,270,173),new(196,56,267,173),new(199,56,264,173)];
            for(int i=0;i<4;i++)result[i+2]=RegisterPcDisplay(Crop(cells[i+2],sourceRects[i]));
            return pc=result;
        }
    }
    internal static BitmapSource[] Bed
    {
        get
        {
            if(bed!=null)return bed;
            var cells=SpriteAtlas.Load("shared-bed-scene-v2",3,2);if(cells.Length!=6)return bed=[];
            var reference=SpriteAtlas.VisibleBounds(cells[0]);if(reference.IsEmpty)return bed=[];
            double scale=240/reference.Width;
            // The generated front cells include the same far footpost. Align that
            // concrete landmark, not each differently folded blanket's alpha center.
            return bed=cells.Select(source=>Register(source,scale)).ToArray();
        }
    }
    internal static (int From,int To,double Blend) Blanket(double realSeconds)
    {
        if(realSeconds<6)return(4,1,Smooth((realSeconds-4)/2)); // pull up, settle
        if(realSeconds>=18)return(1,5,Smooth((realSeconds-18)/2.5)); // fold back to wake
        // A calm4.8s breath: rest -> inhale -> rest -> exhale. Crossfade only
        // authored cloth, with an identical floor/footpost/canvas registration.
        int[] cycle=[1,2,1,3];double p=((realSeconds-6)%4.8)/1.2;int index=(int)p;
        return(cycle[index],cycle[(index+1)%4],Smooth(p-index));
    }
    private static BitmapSource Register(BitmapSource source,double scale)
    {
        var b=SpriteAtlas.VisibleBounds(source);var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        double right=248,bottom=256;
        // Source files stay unchanged. Both source drawing and its alpha remain
        // intact; only the runtime canvas receives this uniform registration.
        double x=right-b.Right*scale,y=bottom-b.Bottom*scale;
        using(var dc=visual.RenderOpen())dc.DrawImage(source,new Rect(x,y,source.PixelWidth*scale,source.PixelHeight*scale));
        var image=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    private static BitmapSource RegisterCanvas(BitmapSource source)
    {
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        using(var dc=visual.RenderOpen())dc.DrawImage(source,new Rect(0,0,256,256));
        var image=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    private static BitmapSource Crop(BitmapSource source,Int32Rect rect)
    {var crop=new CroppedBitmap(source,rect);crop.Freeze();return crop;}
    private static BitmapSource RegisterPcFront(BitmapSource source)
    {
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        using(var dc=visual.RenderOpen())dc.DrawImage(source,new Rect(8,0,258.56,256));
        var image=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    private static BitmapSource RegisterPcDisplay(BitmapSource source)
    {
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        // Measured source display regions enter the actual monitor interior, not
        // the requested prompt rectangle. Outside-display glow is excluded by crop.
        using(var dc=visual.RenderOpen())dc.DrawImage(source,new Rect(126.5,50,80,46.5));
        var image=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    private static double Smooth(double value){value=Math.Clamp(value,0,1);return value*value*(3-2*value);}
}
