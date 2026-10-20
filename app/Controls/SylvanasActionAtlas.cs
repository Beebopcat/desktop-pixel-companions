using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

/// <summary>Fixed source-pixel scale and measured body pivots for the four-scene repair.
/// Full-body art contains the equipment; a bounding box is never used as a fake hand contact.</summary>
internal static class SylvanasActionAtlas
{
    internal sealed record Anchor(double BodyCenterX,double SoleY);
    internal sealed record Bank(string Stem,string Sha256,double BodyReferenceHeight,int[][] Rects,Anchor[] Anchors,double BodyCanvasX=128);
    private static readonly Dictionary<string,BitmapSource[]> Cache=[];
    internal static BitmapSource[] Load(string bank)
    {
        if(Cache.TryGetValue(bank,out var result))return result;
        var metadata=Path.Combine(AppContext.BaseDirectory,"Assets","sylvanas-action-registration.json");
        if(!File.Exists(metadata))return Cache[bank]=[];
        var banks=JsonSerializer.Deserialize<Dictionary<string,Bank>>(File.ReadAllText(metadata),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
        if(banks==null||!banks.TryGetValue(bank,out var spec)||spec.Rects.Length!=12||spec.Anchors.Length!=12||spec.BodyReferenceHeight<=0)return Cache[bank]=[];
        var path=Path.Combine(AppContext.BaseDirectory,"Assets",spec.Stem+".png");
        if(!File.Exists(path)||!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),spec.Sha256,StringComparison.OrdinalIgnoreCase))return Cache[bank]=[];
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(path);image.EndInit();image.Freeze();
        double scale=224/spec.BodyReferenceHeight;
        var frames=new List<BitmapSource>();
        for(int i=0;i<12;i++)
        {
            var rect=spec.Rects[i];if(rect.Length!=4||rect[0]<0||rect[1]<0||rect[2]<=0||rect[3]<=0||rect[0]+rect[2]>image.PixelWidth||rect[1]+rect[3]>image.PixelHeight)return Cache[bank]=[];
            var source=new CroppedBitmap(image,new Int32Rect(rect[0],rect[1],rect[2],rect[3]));source.Freeze();
            var a=spec.Anchors[i];double x=spec.BodyCanvasX-a.BodyCenterX*scale,y=256-a.SoleY*scale;
            var b=SpriteAtlas.VisibleBounds(source);
            if(b.IsEmpty||x+b.Left*scale<0||x+b.Right*scale>256||y+b.Top*scale<0||y+b.Bottom*scale>256.01)return Cache[bank]=[];
            var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
            using(var dc=visual.RenderOpen())dc.DrawImage(source,new Rect(x,y,source.PixelWidth*scale,source.PixelHeight*scale));
            var frame=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);frame.Render(visual);frame.Freeze();
            CompanionGroundRegistration.Remember(frame,256);frames.Add(frame);
        }
        return Cache[bank]=frames.ToArray();
    }
}
