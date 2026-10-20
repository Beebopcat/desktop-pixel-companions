using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

/// <summary>Pinned source-cell ground estimates. These are staging geometry, never visual contact approval.</summary>
internal static class CompanionGroundRegistration
{
    internal sealed record Anchor(string Character,string Bank,int Frame,int Width,int Height,string SourcePixelsSha256,
        double SourcePixelScale,double SoleY,bool VisibleBootSole,string Basis,double BodyCenterX=128,double BodyCanvasX=128);
    private static readonly Lazy<Dictionary<string,Anchor>> Anchors=new(Read);
    private static readonly Dictionary<BitmapSource,double> GroundByImage=[];
    internal static bool TrySource(string character,string bank,int frame,BitmapSource source,out Anchor? anchor)
    {
        anchor=null;
        if(!Anchors.Value.TryGetValue(character+"/"+bank+"/"+frame,out var candidate)||candidate.Width!=source.PixelWidth||candidate.Height!=source.PixelHeight||
            !double.IsFinite(candidate.SourcePixelScale)||candidate.SourcePixelScale<=0||candidate.SourcePixelScale>16||
            !double.IsFinite(candidate.SoleY)||candidate.SoleY<0||candidate.SoleY>source.PixelHeight||
            !double.IsFinite(candidate.BodyCenterX)||candidate.BodyCenterX<0||candidate.BodyCenterX>source.PixelWidth||
            !double.IsFinite(candidate.BodyCanvasX)||candidate.BodyCanvasX<0||candidate.BodyCanvasX>256||PixelHash(source)!=candidate.SourcePixelsSha256)return false;
        anchor=candidate;return true;
    }
    internal static bool HasBank(string character,string bank)=>Anchors.Value.ContainsKey(character+"/"+bank+"/0");
    internal static bool TryBank(string character,string bank,BitmapSource[] sources,out Anchor[] anchors)
    {
        anchors=[];var result=new List<Anchor>();
        for(int i=0;i<sources.Length;i++)
        {
            if(!TrySource(character,bank,i,sources[i],out var anchor))return false;
            result.Add(anchor!);
        }
        if(result.Count==0||result.Any(a=>Math.Abs(a.SourcePixelScale-result[0].SourcePixelScale)>1e-12||Math.Abs(a.BodyCanvasX-result[0].BodyCanvasX)>1e-12))return false;
        anchors=result.ToArray();return true;
    }
    internal static void Remember(BitmapSource source,double groundY)=>GroundByImage[source]=groundY;
    internal static bool TryGround(BitmapSource source,out double groundY)=>GroundByImage.TryGetValue(source,out groundY);
    private static string PixelHash(BitmapSource image)
    {int stride=(image.PixelWidth*image.Format.BitsPerPixel+7)/8;var pixels=new byte[stride*image.PixelHeight];image.CopyPixels(pixels,stride,0);return Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();}
    private static Dictionary<string,Anchor> Read()
    {
        var result=new Dictionary<string,Anchor>(StringComparer.Ordinal);
        var file=Path.Combine(AppContext.BaseDirectory,"Assets","semantic-ground-registration-candidate-20261004.json");
        if(!File.Exists(file))return result;
        try
        {
            var rows=JsonSerializer.Deserialize<Anchor[]>(File.ReadAllText(file),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(rows!=null)foreach(var row in rows)result.Add(row.Character+"/"+row.Bank+"/"+row.Frame,row);
        }
        catch(Exception error) when(error is IOException or JsonException or ArgumentException){result.Clear();}
        return result;
    }
}
