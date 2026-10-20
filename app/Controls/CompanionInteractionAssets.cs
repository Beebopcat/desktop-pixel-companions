using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

/// <summary>Immutable runtime assets and registration adapter, also used by temporal fixtures.</summary>
internal static class CompanionInteractionAssets
{
    private static readonly Dictionary<string,BitmapSource> Objects=new(StringComparer.Ordinal);
    private static readonly Dictionary<string,BitmapSource[]> Pets=new(StringComparer.Ordinal);
    private static readonly Dictionary<BitmapSource,InteractionPoint> RaisedBootCache=[];
    private static readonly Dictionary<BitmapSource,InteractionPoint> ExtendedPalmCache=[];
    internal static InteractionRig Rig(string character,string bank,int frame)
    {
        var r=SpriteAtlas.Registration(character,bank,frame);
        InteractionPoint P(Point p)=>new(p.X/256,p.Y/256);
        var source=SpriteAtlas.Character(character,bank)[frame];var bounds=SpriteAtlas.VisibleBounds(source);
        var rig=new InteractionRig(P(r.Feet),P(r.Seat),P(r.Head),P(r.NearHand),P(r.FarHand),r.ContactsVerified)
        {VisibleBounds=new(bounds.X/256,bounds.Y/256,bounds.Width/256,bounds.Height/256)};
        if(bank=="activity"&&frame==11)rig=rig with{KickFoot=RaisedBoot(source)};
        if(bank=="motion"&&frame==10)rig=rig with{NearHand=ExtendedPalm(source),Verified=false};
        return rig;
    }
    internal static InteractionRig ReferenceRig(string character,CompanionPropDefinition? prop,CompanionPerformanceFrame frame)
    {
        if(prop?.Id=="pc")return Rig(character,"pc",0);
        if(prop?.Id=="soccer")return Rig(character,"activity",11);
        int reference=prop?.Id switch{"table-chair" or "pc"=>0,"couch-tv"=>2,"bed"=>8,_=>6};
        return prop?.Category is "furniture" or "pet" or "fantasy-pet"?Rig(character,"activity",reference):Rig(character,frame.Bank,frame.Frame);
    }
    internal static BitmapSource? Prop(CompanionPropDefinition prop,int frame)
    {
        if(prop.AssetKey.StartsWith("procedural-",StringComparison.Ordinal))return null;
        if(prop.Category is "pet" or "fantasy-pet")
        {
            var key=prop.AssetKey+"/"+prop.CellIndex;
            if(!Pets.TryGetValue(key,out var row))
            {
                var raw=SpriteAtlas.Load(prop.AssetKey,prop.Columns,prop.Rows).Skip(prop.CellIndex).Take(4).ToArray();
                if(raw.Length!=4)return null;
                var first=SpriteAtlas.VisibleBounds(raw[0]);double scale=190/Math.Max(1,first.Height);
                scale=Math.Min(scale,252/Math.Max(1,raw.Max(x=>Math.Max(SpriteAtlas.VisibleBounds(x).Width,SpriteAtlas.VisibleBounds(x).Height))));
                // One scale for all four animal poses, including curled/leaping frames.
                Pets[key]=row=raw.Select(s=>Register(s,scale)).ToArray();
            }
            return row[Math.Clamp(frame,0,3)];
        }
        string objectKey=prop.AssetKey+"/"+(prop.CellIndex+frame);
        if(Objects.TryGetValue(objectKey,out var cached))return cached;
        var atlas=SpriteAtlas.Load(prop.AssetKey,prop.Columns,prop.Rows);
        if(atlas.Length==0)return null;
        var source=atlas[Math.Clamp(prop.CellIndex+frame,0,atlas.Length-1)];var b=SpriteAtlas.VisibleBounds(source);
        if(b.IsEmpty)return source;
        // Preserve the object's aspect ratio inside a fixed logical square.
        return Objects[objectKey]=Register(source,240/Math.Max(b.Width,b.Height),centerVertically:true);
    }
    internal static int PetArtFrame(CompanionPerformanceFrame frame,InteractionRig reference,double actorSide,CompanionPropDefinition? prop=null)
    {
        double handY=1-actorSide*(reference.Feet.Y-reference.NearHand.Y);
        var motion=CompanionPetMotion.Sample(frame,handY);
        if(prop==null)return motion.ArtFrame;
        double upright=.72+.28*PetHead(Prop(prop,0),0).Y,lowered=.72+.28*PetHead(Prop(prop,1),1).Y;
        return CompanionPetMotion.SelectContactPose(motion.ArtFrame,motion.ContactRequested,handY,upright,lowered);
    }
    internal static InteractionPoint PetHead(BitmapSource? bitmap,int frame)
    {
        if(bitmap==null)return new(.25,.35);
        var b=SpriteAtlas.VisibleBounds(bitmap);double x=frame switch{2=>.63,_=>.82};
        double y=frame switch{1=>.55,2=>.25,3=>.57,_=>.12};
        // The neutral contact point is the crown (petting), not its eyes.
        // Preserve the authored animal facing. The reaching hand meets its head;
        // the complete actor/animal composition may be mirrored as one scene.
        return new((b.Left+b.Width*x)/256,(b.Top+b.Height*y)/256);
    }
    private static InteractionPoint RaisedBoot(BitmapSource source)
    {
        if(RaisedBootCache.TryGetValue(source,out var cached))return cached;
        // Estimate the outward boot edge below the outstretched arm. This is
        // lineage-preserving placement, not a claim of verified anatomy.
        var b=SpriteAtlas.VisibleBounds(source);var rgba=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        int stride=rgba.PixelWidth*4;var pixels=new byte[stride*rgba.PixelHeight];rgba.CopyPixels(pixels,stride,0);
        int from=Math.Clamp((int)(b.Top+b.Height*.30),0,rgba.PixelHeight-1),to=Math.Clamp((int)(b.Top+b.Height*.65),from,rgba.PixelHeight-1);
        int right=-1;double yTotal=0;int count=0;
        for(int y=from;y<=to;y++)for(int x=0;x<rgba.PixelWidth;x++)
        {
            if(pixels[y*stride+x*4+3]<=96)continue;
            if(x>right){right=x;yTotal=y;count=1;}else if(x==right){yTotal+=y;count++;}
        }
        return RaisedBootCache[source]=right<0?new(.85,.60):new((right+1)/256.0,(yTotal/count+.5)/256.0);
    }
    private static InteractionPoint ExtendedPalm(BitmapSource source)
    {
        if(ExtendedPalmCache.TryGetValue(source,out var cached))return cached;
        // The extended-arm pose places its palm above the old generic wrist
        // registration. Estimate the outer hand edge without altering source art.
        // This remains an inferred anchor, never certified speaker/animation QA.
        var b=SpriteAtlas.VisibleBounds(source);var rgba=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        int stride=rgba.PixelWidth*4;var pixels=new byte[stride*rgba.PixelHeight];rgba.CopyPixels(pixels,stride,0);
        int from=Math.Clamp((int)(b.Top+b.Height*.15),0,rgba.PixelHeight-1),to=Math.Clamp((int)(b.Top+b.Height*.50),from,rgba.PixelHeight-1);
        int right=-1;double yTotal=0;int count=0;
        for(int y=from;y<=to;y++)for(int x=0;x<rgba.PixelWidth;x++)
        {
            if(pixels[y*stride+x*4+3]<=96)continue;
            if(x>right){right=x;yTotal=y;count=1;}else if(x==right){yTotal+=y;count++;}
        }
        return ExtendedPalmCache[source]=right<0?new(.88,.39):new(Math.Max(0,right-5)/256.0,(yTotal/count+.5)/256.0);
    }
    private static BitmapSource Register(BitmapSource source,double scale,bool centerVertically=false)
    {
        var b=SpriteAtlas.VisibleBounds(source);var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        double x=128-(b.Left+b.Width/2)*scale;
        double y=centerVertically?128-(b.Top+b.Height/2)*scale:256-b.Bottom*scale;
        using(var dc=visual.RenderOpen())dc.DrawImage(source,new Rect(x,y,source.PixelWidth*scale,source.PixelHeight*scale));
        var result=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
    }
}
