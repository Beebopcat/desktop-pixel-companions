using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Security.Cryptography;
using System.Text.Json;

namespace AzerothCompanion.Controls;

/// <summary>Loads immutable alpha-preserving cells. Authored sheets may have unequal row layouts.</summary>
internal static class SpriteAtlas
{
    internal sealed record FrameRegistration(Rect VisibleBounds,Point Feet,Point Seat,Point Head,Point NearHand,Point FarHand,bool ContactsVerified);
    internal static FrameRegistration Registration(string characterId,string bank,int frame)
    {
        var frames=Character(characterId,bank);if(frames.Length==0)return new(Rect.Empty,new(),new(),new(),new(),new(),false);
        var b=VisibleBounds(frames[Math.Clamp(frame,0,frames.Length-1)]);
        Point P(double x,double y)=>new(b.Left+b.Width*x,b.Top+b.Height*y);
        var feet=P(.5,1);var seat=P(.52,.58);var head=P(.52,.18);var hand=P(.74,.53);var other=P(.36,.53);
        if(bank=="activity")
        {
            if(frame is 0 or 1 or 2 or 3){seat=P(.54,.59);feet=P(.64,1);head=P(.54,.17);hand=P(frame==1?.80:.70,.42);other=P(.52,.43);}
            else if(frame is 4 or 5){hand=P(.66,.39);other=P(.45,.40);}
            else if(frame is 6 or 7){seat=P(.43,.82);head=P(.55,.22);hand=P(frame==6?.89:.94,frame==6?.71:.52);other=P(.50,.71);}
            else if(frame==8)
            {
                bool headLeft=characterId is "rowan" or "thrall" or "palpatine" or "vi" or "jinx" or "sylvanas"; // jaina: v1 sleeping sprite faces the other way
                head=P(headLeft?.17:.83,.35);feet=P(headLeft?.91:.09,.78);seat=P(.50,.66);hand=P(headLeft?.22:.78,.62);other=hand;
            }
            if(frame is 0 or 1 or 2 or 3&&characterId is "thrall" or "palpatine")
            {
                seat=P(.40,.62);head=P(.49,.22);
                hand=frame switch{0=>P(.78,.55),1=>P(.82,.45),_=>P(.65,.43)};
                other=frame switch{0=>P(.43,.56),1=>P(.55,.45),_=>P(.48,.44)};
            }
            if(frame is 6 or 7)
            {
                hand=(characterId,frame) switch
                {
                    ("thrall",6)=>P(.92,.68),("thrall",7)=>P(.90,.40),
                    ("palpatine",6)=>new Point(232,204),("palpatine",7)=>P(.94,.48),
                    ("silco",6)=>P(.88,.88),("silco",7)=>P(.95,.62),
                    _=>hand
                };
            }
            if(frame is 0 or 1)
            {
                hand=(characterId,frame) switch
                {("rowan",0)=>P(.65,.51),("rowan",1)=>P(.73,.49),
                 ("silco",0)=>P(.78,.51),("silco",1)=>P(.80,.51),_=>hand};
            }
        }
        else if(bank=="pc")
        {
            var referenceBounds=VisibleBounds(frames[0]);
            seat=new(referenceBounds.Left+referenceBounds.Width*.41,referenceBounds.Top+referenceBounds.Height*.62);
            feet=P(.68,1);head=P(.48,.18);
            hand=P(.87,.46);other=P(.70,.44);
        }
        else if(bank=="motion"&&frame==10){hand=P(.88,.49);other=P(.47,.67);}
        else if(bank=="motion"&&frame==11){hand=P(.74,.47);other=P(.55,.51);}
        // Codex measured these points on the previous art. Redesigned sheets use the proportional estimates above until re-measured.
        if(characterId=="sylvanas"||characterId=="jinx"&&bank is "activity" or "pc"||characterId=="vi"&&bank=="pc")
        {
            // Pixel estimates from the registered v4 source-cell review, not targets
            // invented by a contact solver. Keep a common pelvis across adjacent
            // seated poses and the same ground pivot across kneeling poses.
            if(bank=="activity")
            {
                if(frame is >=0 and <=3)
                {
                    (seat,head) = characterId switch
                    {
                        "sylvanas"=>(new Point(120,184),new Point(132,104)),
                        "jaina"=>(new Point(122,176),new Point(126,86)),
                        "vi"=>(new Point(111,179),new Point(116,89)),
                        _=>(new Point(133,179),new Point(142,88))
                    };
                    (hand,other)=(characterId,frame) switch
                    {
                        ("sylvanas",0)=>(new Point(169,164),new Point(145,164)),
                        ("sylvanas",1)=>(new Point(173,151),new Point(151,153)),
                        ("sylvanas",2)=>(new Point(167,165),new Point(151,163)),
                        ("jaina",0)=>(new Point(179,150),new Point(157,153)),
                        ("jaina",1)=>(new Point(168,140),new Point(151,142)),
                        ("jaina",2)=>(new Point(171,150),new Point(155,151)),
                        ("vi",0)=>(new Point(156,162),new Point(136,157)),
                        ("vi",1)=>(new Point(156,149),new Point(138,150)),
                        ("vi",2)=>(new Point(152,153),new Point(135,155)),
                        ("jinx",0)=>(new Point(183,168),new Point(163,163)),
                        ("jinx",1)=>(new Point(188,145),new Point(170,148)),
                        ("jinx",2)=>(new Point(177,162),new Point(164,165)),
                        _=>(hand,other)
                    };
                }
                else if(frame is 6 or 7)
                {
                    feet=new(128,256);
                    (seat,head,hand,other)=(characterId,frame) switch
                    {
                        ("sylvanas",6)=>(new Point(125,194),new Point(166,126),new Point(195,213),new Point(167,184)),
                        ("sylvanas",7)=>(new Point(125,194),new Point(149,118),new Point(198,132),new Point(156,185)),
                        ("jaina",6)=>(new Point(112,224),new Point(149,137),new Point(205,221),new Point(151,205)),
                        ("jaina",7)=>(new Point(112,224),new Point(151,137),new Point(207,176),new Point(149,197)),
                        ("vi",6)=>(new Point(106,220),new Point(151,142),new Point(195,228),new Point(140,186)),
                        ("vi",7)=>(new Point(104,220),new Point(125,132),new Point(198,162),new Point(94,217)),
                        ("jinx",6)=>(new Point(107,225),new Point(156,142),new Point(212,219),new Point(124,223)),
                        _=>(new Point(107,225),new Point(156,139),new Point(213,174),new Point(122,217))
                    };
                }
                else if(frame==8)
                {
                    head=characterId switch{"sylvanas"=>new(54,222),"jaina"=>new(52,217),"vi"=>new(62,220),_=>new(55,220)};
                    hand=characterId switch{"sylvanas"=>new(64,246),"jaina"=>new(55,245),"vi"=>new(71,246),_=>new(47,250)};
                    other=hand;
                }
            }
            else if(bank=="pc")
            {
                int pose=frame switch{1 or 4 or 7 or 9=>1,3 or 5 or 8 or 11=>2,_=>0};
                (seat,head)=characterId switch
                {
                    "sylvanas"=>(new Point(137,188),new Point(141,102)),
                    "jaina"=>(new Point(137,189),new Point(141,101)),
                    "vi"=>(new Point(95,200),new Point(126,105)),
                    _=>(new Point(137,194),new Point(135,102))
                };
                (hand,other)=(characterId,pose) switch
                {
                    ("sylvanas",0)=>(new Point(183,151),new Point(166,157)),
                    ("sylvanas",1)=>(new Point(183,149),new Point(165,155)),
                    ("sylvanas",2)=>(new Point(186,152),new Point(168,157)),
                    ("jaina",0)=>(new Point(183,141),new Point(164,145)),
                    ("jaina",1)=>(new Point(181,141),new Point(164,145)),
                    ("jaina",2)=>(new Point(182,141),new Point(163,145)),
                    ("vi",0)=>(new Point(161,149),new Point(145,154)),
                    ("vi",1)=>(new Point(160,148),new Point(145,152)),
                    ("vi",2)=>(new Point(163,149),new Point(147,154)),
                    ("jinx",0)=>(new Point(179,145),new Point(163,152)),
                    ("jinx",1)=>(new Point(181,146),new Point(163,152)),
                    _=>(new Point(181,148),new Point(165,153))
                };
            }
        }
        // Registration estimates are explicit, not falsely treated as hand-authored
        // contact approval. The interaction renderer and review can refine these.
        return new(b,feet,seat,head,hand,other,false);
    }
    /// <summary>The user's chosen redesigns. Males and Sylvanas are unchanged; Jinx activity keeps its black-top design (the red-jacket attempt1 sheet is a different look).</summary>
    internal static string? ChosenStem(string id,string bank)=>(id,bank) switch
    {
        ("jaina","basic")=>"jaina-pixel-v1",("jaina","motion")=>"jaina-motion-v1",("jaina","activity")=>"jaina-activity-v1",("jaina","weapons")=>"jaina-weapons-v1",
        ("jinx","basic")=>"jinx-pixel-v4",("jinx","motion")=>"jinx-motion-v4",("jinx","activity")=>"jinx-activity-v4-gutter",("jinx","weapons")=>"jinx-weapons-v4-attempt1",
        ("vi","basic")=>"vi-pixel-v4",("vi","motion")=>"vi-motion-v4",("vi","activity")=>"vi-activity-v4-attempt1",("vi","weapons")=>"vi-weapons-v4-attempt1",
        _=>null
    };
    private static readonly Dictionary<string, BitmapSource[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private sealed record FrameSource(string Stem,string SourceSha256,int[] SourceRect);
    private sealed record CropLayout(int Width,int Height,int Columns,int Rows,string SourceSha256,int[][] Rects,Dictionary<int,FrameSource>? FrameSourceOverrides=null);
    private static readonly Lazy<Dictionary<string,CropLayout>> CropLayouts = new(ReadCropLayouts);
    internal static BitmapSource[] Load(string stem, int columns, int rows)
    {
        var key = stem + "/" + columns + "/" + rows;
        if (Cache.TryGetValue(key, out var loaded)) return loaded;
        var image = Open(stem); if (image == null) return [];
        if(TryAuthoredCrops(stem,image,columns,rows,out var authored))return Cache[key]=authored;
        if(CropLayouts.Value.ContainsKey(stem))return Cache[key]=[];
        var result = new List<BitmapSource>();
        for (int row = 0; row < rows; row++) for (int col = 0; col < columns; col++)
            result.Add(Crop(image, col * image.PixelWidth / columns, row * image.PixelHeight / rows,
                (col + 1) * image.PixelWidth / columns, (row + 1) * image.PixelHeight / rows));
        return Cache[key] = result.ToArray();
    }
    internal static BitmapSource[] Character(string id, string bank)
    {
        if(id=="sylvanas"&&bank is CompanionSylvanasActions.StepsBank or CompanionSylvanasActions.HandlingBank)return SylvanasActionAtlas.Load(bank);
        var key=id+"/registered/"+bank;
        if(Cache.TryGetValue(key,out var registered))return registered;
        var raw=RawCharacter(id,bank);
        if(raw.Length==0)return [];
        // Every pose occupies the same logical canvas. A bank has ONE source-pixel
        // scale, measured from its standing poses; crouching or lying down does not
        // make the head/body expand to fill the old individually fitted cell.
        int[] referenceFrames=bank switch{"motion"=>[0,1,2,3,4,5,6,7],"activity"=>[4],"weapons"=>[0,3,6,9],_=>[0,1,2,3]};
        var heights=referenceFrames.Where(i=>i<raw.Length).Select(i=>VisibleBounds(raw[i]).Height).Where(h=>h>0).OrderBy(h=>h).ToArray();
        if(heights.Length==0)return Cache[key]=raw;
        double referenceHeight=heights[heights.Length/2],scale=(bank=="pc"?182:224)/referenceHeight;
        // A sprawling sleep/kick drawing must not rescale every standing frame.
        // Such authored pose extremes receive a fixed import calibration; runtime
        // animation never changes scale or independently Uniform-fits a cell.
        bool pinned=CompanionGroundRegistration.HasBank(id,bank);
        CompanionGroundRegistration.Anchor[] anchors=[];
        // User-chosen redesign art carries no Codex-measured anchors; use automatic registration for it.
        if(pinned&&!CompanionGroundRegistration.TryBank(id,bank,raw,out anchors)){if(ChosenStem(id,bank)==null)return Cache[key]=[];pinned=false;anchors=[];}
        var result=raw.Select((source,frame)=>pinned?
            Register(source,anchors[frame].SourcePixelScale,anchors[frame].SoleY,anchors[frame].BodyCenterX,anchors[frame].BodyCanvasX):
            Register(source,Math.Min(scale,252/Math.Max(1,Math.Max(VisibleBounds(source).Width,VisibleBounds(source).Height))))).ToArray();
        if(bank=="activity"&&id is "thrall" or "palpatine")
        {
            var seated=Load("shared-seated-v2",3,4);int row=id switch{"thrall"=>0,"palpatine"=>1,"vi"=>2,_=>3};
            if(seated.Length==12)
            {
                var cells=seated.Skip(row*3).Take(3).ToArray();double seatScale=182/VisibleBounds(cells[0]).Height;
                for(int i=0;i<4;i++)result[i]=Register(cells[Math.Min(i,2)],seatScale);
            }
            if(id is "vi" or "jinx")result[5]=result[4];
        }
        return Cache[key]=result;
    }
    internal static Rect VisibleBounds(BitmapSource source)
    {
        if(BoundsCache.TryGetValue(source,out var bounds))return bounds;
        var bitmap=source.Format==PixelFormats.Bgra32||source.Format==PixelFormats.Pbgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        int stride=bitmap.PixelWidth*4;var pixels=new byte[stride*bitmap.PixelHeight];bitmap.CopyPixels(pixels,stride,0);
        int left=bitmap.PixelWidth,top=bitmap.PixelHeight,right=-1,bottom=-1;
        for(int y=0;y<bitmap.PixelHeight;y++)for(int x=0;x<bitmap.PixelWidth;x++)
            if(pixels[y*stride+x*4+3]>96){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
        return BoundsCache[source]=right<left?Rect.Empty:new Rect(left,top,right-left+1,bottom-top+1);
    }
    private static readonly Dictionary<BitmapSource,Rect> BoundsCache=[];
    private static BitmapSource Register(BitmapSource source,double scale,double? semanticSoleY=null,double? bodyCenterX=null,double bodyCanvasX=128)
    {
        var bounds=VisibleBounds(source);if(bounds.IsEmpty)return source;
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        // Registration changes layout only. Authored pixels, alpha and source
        // files remain intact, and all source frames keep their aspect ratio.
        double x=Math.Round(bodyCanvasX-(bodyCenterX??(bounds.Left+bounds.Width/2))*scale),y=256-(semanticSoleY??bounds.Bottom)*scale;
        if(semanticSoleY!=null&&(x+bounds.Left*scale<0||x+bounds.Right*scale>256.01||y+bounds.Top*scale<-.01||y+bounds.Bottom*scale>256.01))
            throw new InvalidOperationException("Source art exceeds its pinned body/sole canvas. Repair the source rather than resize or trim the actor.");
        using(var dc=visual.RenderOpen())dc.DrawImage(source,new Rect(x,y,source.PixelWidth*scale,source.PixelHeight*scale));
        var result=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();
        if(semanticSoleY!=null){CompanionGroundRegistration.Remember(result,256);return result;}
        var rendered=VisibleBounds(result);
        if(!rendered.IsEmpty&&rendered.Bottom<256)
        {
            var aligned=new DrawingVisual();RenderOptions.SetBitmapScalingMode(aligned,BitmapScalingMode.NearestNeighbor);
            using(var dc=aligned.RenderOpen())dc.DrawImage(result,new Rect(0,256-rendered.Bottom,256,256));
            var grounded=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);grounded.Render(aligned);grounded.Freeze();return grounded;
        }
        return result;
    }
    private static BitmapSource[] RawCharacter(string id, string bank)
    {
        // Optional banks must never silently impersonate readiness using idle art.
        if(bank=="pc")
        {
            string[] men=["rowan","thrall","palpatine","silco"];
            string[] women=["sylvanas","jaina","vi","jinx"];
            if(id=="jaina")
            {
                // The shared PC sheet carries the older Jaina design; seat her with the chosen redesign instead.
                var seated=Load("jaina-activity-v1",3,4);if(seated.Length!=12)return [];
                int[] seatedSequence=[0,1,0,2,1,2,0,1,2,1,0,2];
                return seatedSequence.Select(i=>seated[i]).ToArray();
            }
            int row=Array.IndexOf(men,id);string stem="shared-male-pc-v2";
            if(row<0){row=Array.IndexOf(women,id);stem="shared-female-pc-v4-final";}
            if(row<0)return [];
            var seatedPc=Load(stem,3,4);if(seatedPc.Length!=12)return [];
            int[] sequence=[0,1,0,2,1,2,0,1,2,1,0,2];
            return sequence.Select(i=>seatedPc[row*3+i]).ToArray();
        }
        if(bank is not ("basic" or "motion" or "activity" or "weapons"))return [];
        if(ChosenStem(id,bank) is string chosenStem)
        {
            var chosen=Load(chosenStem,3,4);
            if(chosen.Length==12)return chosen;
        }
        if(id is "sylvanas" or "jaina" or "vi" or "jinx")
        {
            string suffix=bank switch{"basic"=>"pixel-v4","activity"=>id=="jinx"?"activity-v4-gutter":"activity-v4-final",_=>bank+"-v4"};
            if(id=="vi"&&bank is "basic" or "motion")suffix=bank=="basic"?"pixel-v5":"motion-v5";
            var revised=Load(id+"-"+suffix,3,4);
            if(revised.Length==12)
            {
                // Preserve the staff during the orb hold. The approved raised-palm
                // pose is reused while the separate spell effect animates.
                if(id=="jaina"&&bank=="weapons")revised[7]=revised[6];
                return revised;
            }
        }
        if(bank=="weapons")return Load(id+"-weapons-v1",3,4);
        if(id=="silco")
        {
            var refined=Load("silco-"+(bank=="basic"?"pixel":bank)+"-v2",3,4);
            if(refined.Length==12)return refined;
        }
        var separate = Load(id + (bank == "basic" ? "-pixel-v1" : "-" + bank + "-v1"), 3, 4);
        if (separate.Length == 12) return separate;
        var key = id + "/full";
        if (!Cache.TryGetValue(key, out var full))
        {
            var image = Open(id + "-full-v1"); if (image == null) return [];
            if(TryAuthoredCrops(id+"-full-v1",image,0,6,out var authored))
            {
                Cache[key]=authored;
                int offset=bank=="motion"?12:bank=="activity"?24:0;
                return authored.Skip(offset).Take(12).ToArray();
            }
            // These boundaries reflect the actual generated atlas, not its requested grid.
            (int[] Counts, double[] Ends) layout = id switch
            {
                "thrall" => ([6,6,8,6,6,4], [0,214/1263d,438/1263d,660/1263d,864/1263d,1060/1263d,1]),
                "palpatine" => ([6,6,8,6,6,4], [0,219/1254d,439/1254d,651/1254d,846/1254d,1053/1254d,1]),
                "vi" => ([6,6,8,4,6,6], [0,230/1278d,459/1278d,690/1278d,884/1278d,1058/1278d,1]),
                "jinx" => ([6,6,6,6,8,4], [0,220/1263d,438/1263d,650/1263d,858/1263d,1064/1263d,1]),
                _ => ([6,6,6,6,6,6], [0,235/1230d,461/1230d,666/1230d,868/1230d,1053/1230d,1])
            };
            var cells = new List<BitmapSource>();
            for (int row=0;row<layout.Counts.Length;row++) for(int col=0;col<layout.Counts[row];col++)
                cells.Add(Crop(image, col*image.PixelWidth/layout.Counts[row], (int)Math.Round(layout.Ends[row]*image.PixelHeight),
                    (col+1)*image.PixelWidth/layout.Counts[row], (int)Math.Round(layout.Ends[row+1]*image.PixelHeight)));
            Cache[key] = full = cells.ToArray();
        }
        int start = bank == "motion" ? 12 : bank == "activity" ? 24 : 0;
        return full.Skip(start).Take(12).ToArray();
    }
    private static BitmapImage? Open(string stem)
    {
        var path = Path.Combine(AppContext.BaseDirectory,"Assets",stem+".png");
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad;
            image.UriSource=new Uri(path); image.EndInit(); image.Freeze(); return image;
        }
        catch(Exception e) when(e is IOException or NotSupportedException or FormatException or ArgumentException){return null;}
    }
    private static Dictionary<string,CropLayout> ReadCropLayouts()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Assets","atlas-crops.json");
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>128*1024)return new();
            return JsonSerializer.Deserialize<Dictionary<string,CropLayout>>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new();
        }
        catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){return new();}
    }
    private static bool TryAuthoredCrops(string stem,BitmapSource image,int columns,int rows,out BitmapSource[] cells)
    {
        cells=[];
        if(!CropLayouts.Value.TryGetValue(stem,out var layout)||layout.Width!=image.PixelWidth||layout.Height!=image.PixelHeight||
            layout.Columns!=columns||layout.Rows!=rows||layout.Rects==null||layout.Rects.Length is <1 or >64)return false;
        if(columns>0&&layout.Rects.Length!=columns*rows||columns==0&&layout.Rects.Length!=36)return false;
        try
        {
            // Metadata is bound to the exact immutable source. Regenerated artwork
            // must receive reviewed rectangles rather than silently reusing old ones.
            var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets",stem+".png"))));
            if(!hash.Equals(layout.SourceSha256,StringComparison.OrdinalIgnoreCase))return false;
        }
        catch(IOException){return false;}
        if(layout.Rects.Any(r=>r==null||r.Length!=4||r[0]<0||r[1]<0||r[2]<1||r[3]<1||r[0]+r[2]>image.PixelWidth||r[1]+r[3]>image.PixelHeight))return false;
        cells=layout.Rects.Select(r=>(BitmapSource)Crop(image,r[0],r[1],r[0]+r[2],r[1]+r[3])).ToArray();
        if(layout.FrameSourceOverrides!=null)
        {
            foreach(var (index,source) in layout.FrameSourceOverrides)
            {
                if(index<0||index>=cells.Length||source.Stem!=Path.GetFileName(source.Stem))return false;
                var alternate=Open(source.Stem);var r=source.SourceRect;
                if(alternate==null||r.Length!=4||r[0]<0||r[1]<0||r[2]<1||r[3]<1||r[0]+r[2]>alternate.PixelWidth||r[1]+r[3]>alternate.PixelHeight)return false;
                var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets",source.Stem+".png"))));
                if(!hash.Equals(source.SourceSha256,StringComparison.OrdinalIgnoreCase))return false;
                cells[index]=Crop(alternate,r[0],r[1],r[0]+r[2],r[1]+r[3]);
            }
        }
        return true;
    }
    private static BitmapSource Crop(BitmapSource image,int left,int top,int right,int bottom)
    {
        var cell=new CroppedBitmap(image,new Int32Rect(left,top,Math.Max(1,right-left),Math.Max(1,bottom-top)));
        cell.Freeze();return cell;
    }
}
