using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AzerothCompanion.Controls;
using AzerothCompanion.Core;

namespace AzerothCompanion;

public static partial class VisualPreview
{
    private const int MotionLogicalSide=188,MotionNativeSide=47,MotionSamples=24;
    private sealed record MotionClip(string Id,string Name,string Category,double Duration,bool Weapon);
    private sealed record MotionAlpha(int Count,int Left,int Top,int Right,int Bottom,int TopAndSideEdgePixels,int FloorPixels);
    private sealed record MotionSample(int Index,double VirtualSeconds,double RealSeconds,string Phase,bool Done,
        double ActorScale,double ActorAngle,MotionAlpha NativeAlpha,MotionAlpha InspectionAlpha,string NativeHash,string InspectionHash,object? Contact);
    private sealed record MotionEntry(string CharacterId,string Character,string Id,string Name,string Category,bool Weapon,
        double Duration,double RealDuration,string NativeStrip,string InspectionStrip,int DistinctNativeFrames,int DistinctInspectionFrames,IReadOnlyList<MotionSample> Samples,bool Available=true,string? UnavailableReason=null)
    { public int SampleCount=>Samples.Count;public int SheetColumns=>Math.Min(MotionSamples,Samples.Count); }

    internal static async Task RenderContactProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        const int columnWidth=250,rowHeight=235,header=75,left=16,columns=5;
        int width=left*2+columns*columnWidth,height=header+Characters.All.Length*rowHeight;
        var portrait=new PortraitControl{Width=MotionLogicalSide,Height=MotionLogicalSide};portrait.Compact();MotionLayout(portrait);
        var method=typeof(PortraitControl).GetMethod("ReviewGeometryEvidence",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        var records=new List<object>();int renderedCases=0,unavailableCases=0;
        foreach(bool facingLeft in new[]{false,true})
        {
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20,25,33)),null,new Rect(0,0,width,height));
                DrawText(dc,"Core contact probe · "+(facingLeft?"facing left":"facing right"),left,12,23,Brushes.White);
                DrawText(dc,"188px runtime composition + adjacent actual47px view · inferred contact anchors require visual review",left,45,12,Brushes.LightSlateGray);
                for(int row=0;row<Characters.All.Length;row++)
                {
                    var character=Characters.All[row];portrait.SetCharacter(character);portrait.SetState("quiet");portrait.SetState("idle");portrait.SetRoamingMotion(false,facingLeft);MotionLayout(portrait);
                    var catalog=new CompanionPerformance(character.Id);
                    var selected=new[]{("furniture.bed",.52),("furniture.table-chair",.52),("furniture.pc",.55),
                        (catalog.Definitions.First(d=>d.Category=="pet"&&d.Id.EndsWith(".greet",StringComparison.Ordinal)).Id,.70),
                        (catalog.Definitions.First(d=>d.Category=="pet"&&d.Id.EndsWith(".play",StringComparison.Ordinal)).Id,.52)};
                    DrawText(dc,character.Name,left,header+row*rowHeight,14,Brushes.White);
                    for(int col=0;col<selected.Length;col++)
                    {
                        var (id,portion)=selected[col];var definition=catalog.Definitions.Single(d=>d.Id==id);double elapsed=definition.Duration*portion;
                        double x=left+col*columnWidth,y=header+row*rowHeight+22;
                        if(!portrait.IsAnimationAvailable(id))
                        {
                            var reason=portrait.AnimationUnavailableReason(id);unavailableCases++;
                            DrawText(dc,"Artwork unavailable",x,y+70,13,Brushes.Khaki);DrawText(dc,id,x,y+MotionLogicalSide+4,10,Brushes.LightSlateGray);
                            records.Add(new{characterId=character.Id,character=character.Name,facingLeft,clipId=id,available=false,unavailableReason=reason,rendered=false});continue;
                        }
                        portrait.RenderAnimationAt(id,elapsed);MotionLayout(portrait);portrait.RenderAnimationAt(id,elapsed);MotionLayout(portrait);
                        var inspection=MotionSnapshot(portrait,MotionLogicalSide);var native=MotionSnapshot(portrait,MotionNativeSide);
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30,38,48)),null,new Rect(x,y,MotionLogicalSide,MotionLogicalSide));
                        dc.DrawImage(inspection,new Rect(x,y,MotionLogicalSide,MotionLogicalSide));
                        dc.DrawImage(native,new Rect(x+MotionLogicalSide+6,y+MotionLogicalSide-MotionNativeSide,MotionNativeSide,MotionNativeSide));
                        dc.DrawLine(new Pen(Brushes.OliveDrab,1),new Point(x,y+MotionLogicalSide),new Point(x+MotionLogicalSide+6+MotionNativeSide,y+MotionLogicalSide));
                        DrawText(dc,id,x,y+MotionLogicalSide+4,10,Brushes.LightSlateGray);
                        object? contact=method?.Invoke(portrait,null);if(contact!=null)contact=JsonSerializer.SerializeToElement(contact);
                        renderedCases++;records.Add(new{characterId=character.Id,character=character.Name,facingLeft,clipId=id,available=true,virtualSeconds=elapsed,nativeAlpha=MotionAlphaBounds(native),inspectionAlpha=MotionAlphaBounds(inspection),contact});
                    }
                }
            }
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            SaveBitmap(bitmap,Path.Combine(directory,"CoreContacts-"+(facingLeft?"left":"right")+".png"));
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        File.WriteAllText(Path.Combine(directory,"core-contacts.json"),JsonSerializer.Serialize(new{rendered=true,cases=records.Count,renderedCases,unavailableCases,allPlannedCasesRendered=unavailableCases==0,logicalSide=MotionLogicalSide,nativeSide=MotionNativeSide,contactEvidenceAvailable=method!=null,
            records,gameCaptured=false,windowShown=false,networkRequests=0,modelCalls=0,audioPlayed=false,note="Contact errors measure inferred anchor constraints; they are not subjective approval."},new JsonSerializerOptions{WriteIndented=true}));
    }

    /// <summary>Renders only app-owned visuals into transparent PNGs. No window is shown, no
    /// desktop/game is captured, and no voice, model, microphone or network service is created.</summary>
    public static async Task RenderMotionReviewAsync(string directory,bool coreOnly=false,string? onlyCharacter=null,IReadOnlySet<string>? onlyClipIds=null)
    {
        // A fixture-owned scope allows a bounded repair review through the existing
        // command without exposing unfinished motion in the desktop application.
        var scopePath=Path.Combine(directory,"review-scope.json");
        if(onlyCharacter==null&&onlyClipIds==null&&File.Exists(scopePath))
        {
            using var scope=JsonDocument.Parse(File.ReadAllText(scopePath));
            if(scope.RootElement.TryGetProperty("character",out var character))onlyCharacter=character.GetString();
            if(scope.RootElement.TryGetProperty("clips",out var clips))
                onlyClipIds=clips.EnumerateArray().Select(clip=>clip.GetString()??"").ToHashSet(StringComparer.Ordinal);
            if(onlyCharacter!=null&&!Characters.All.Any(character=>character.Id==onlyCharacter))throw new InvalidOperationException("Unknown review character.");
        }
        Directory.CreateDirectory(directory);Directory.CreateDirectory(Path.Combine(directory,"strips"));
        var gaitChecks=MotionGaitChecks(directory,onlyCharacter);
        var portrait=new PortraitControl{Width=MotionLogicalSide,Height=MotionLogicalSide};portrait.Compact();
        MotionLayout(portrait);
        var entries=new List<MotionEntry>();var checks=new List<string>();var warnings=new List<object>();
        var evidenceMethod=typeof(PortraitControl).GetMethod("ReviewGeometryEvidence",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        var sampleCount=0;
        foreach(var character in Characters.All)
        {
            if(onlyCharacter!=null&&character.Id!=onlyCharacter)continue;
            portrait.SetCharacter(character);portrait.SetState("quiet");portrait.SetState("idle");portrait.SetRoamingMotion(false,false);MotionLayout(portrait);
            var performance=new CompanionPerformance(character.Id);var weapons=new CompanionWeaponAnimation(character.Id);
            var registered=new[]{"basic","motion","activity"}.Select(bank=>SpriteAtlas.Character(character.Id,bank)).ToArray();
            if(registered.Any(bank=>bank.Length!=12)||registered.SelectMany(bank=>bank).Select(frame=>(frame.PixelWidth,frame.PixelHeight)).Distinct().Count()!=1)
                throw new InvalidOperationException(character.Id+" is missing a registered 12-pose bank or its logical canvases differ.");
            if(performance.Definitions.Count!=70)throw new InvalidOperationException(character.Id+" does not expose 70 base clips.");
            var clips=performance.Definitions.Select(d=>new MotionClip(d.Id,d.Name,d.Category,d.Duration,false))
                .Concat(weapons.Definitions.Select(d=>new MotionClip(d.Id,d.Name,"weapon",d.Duration,true))).ToArray();
            if(onlyClipIds!=null)clips=clips.Where(c=>onlyClipIds.Contains(c.Id)).ToArray();
            if(coreOnly)
            {
                var ids=new[]{"furniture.bed","furniture.table-chair","furniture.pc",
                    clips.First(c=>c.Category=="pet"&&c.Id.EndsWith(".greet",StringComparison.Ordinal)).Id,
                    clips.First(c=>c.Category=="pet"&&c.Id.EndsWith(".play",StringComparison.Ordinal)).Id};
                clips=ids.Select(id=>clips.Single(c=>c.Id==id)).ToArray();
            }
            foreach(var clip in clips)
            {
                if(!portrait.IsAnimationAvailable(clip.Id))
                {
                    var reason=portrait.AnimationUnavailableReason(clip.Id)??"Required artwork is unavailable.";
                    entries.Add(new(character.Id,character.Name,clip.Id,clip.Name,clip.Category,clip.Weapon,clip.Duration,clip.Duration/PortraitControl.AnimationSpeed,"","",0,0,Array.Empty<MotionSample>(),false,reason));
                    warnings.Add(new{characterId=character.Id,clipId=clip.Id,kind="unavailable-scene",reason});continue;
                }
                int clipSamples=clip.Duration>=36?144:clip.Duration>=18?72:clip.Duration>=9?48:MotionSamples;
                var samples=new List<MotionSample>();var nativeFrames=new List<BitmapSource>();var inspectionFrames=new List<BitmapSource>();
                for(int index=0;index<clipSamples;index++)
                {
                    double elapsed=index==clipSamples-1?clip.Duration:clip.Duration*index/(clipSamples-1);
                    portrait.RenderAnimationAt(clip.Id,elapsed);MotionLayout(portrait);
                    // Reapply after the new image has its final arranged dimensions. Both
                    // views use this same 188px logical composition, just like the desktop Viewbox.
                    portrait.RenderAnimationAt(clip.Id,elapsed);MotionLayout(portrait);
                    var inspection=MotionSnapshot(portrait,MotionLogicalSide);var native=MotionSnapshot(portrait,MotionNativeSide);
                    var inspectionAlpha=MotionAlphaBounds(inspection);var nativeAlpha=MotionAlphaBounds(native);
                    if(inspectionAlpha.Count==0||nativeAlpha.Count==0)throw new InvalidOperationException(character.Id+" / "+clip.Id+" / "+index+" is blank.");
                    double scale,angle;bool done;string phase;
                    if(clip.Weapon)
                    {
                        weapons.Start(clip.Id,0);var sampled=weapons.Sample(elapsed);scale=sampled.ActorScale;angle=sampled.ActorAngle;done=sampled.Done;phase=sampled.Phase.ToString();
                    }
                    else
                    {
                        performance.Start(clip.Id,0);var sampled=performance.Sample(elapsed);scale=sampled.ActorScale;angle=sampled.ActorAngle;done=sampled.Done;phase=sampled.Phase.ToString();
                    }
                    if(Math.Abs(scale-1)>1e-10||Math.Abs(angle)>1e-10)throw new InvalidOperationException(character.Id+" / "+clip.Id+" changes whole-actor scale or rotation.");
                    object? contact=evidenceMethod?.Invoke(portrait,null);
                    // Materialize evidence immediately: mutable visual references never escape a frame.
                    if(contact!=null)contact=JsonSerializer.SerializeToElement(contact);
                    if(contact is JsonElement contactJson&&contactJson.TryGetProperty("bodyClipped",out var clipped)&&clipped.ValueKind==JsonValueKind.True)
                        warnings.Add(new{characterId=character.Id,clipId=clip.Id,kind="runtime-body-clipped",frame=index});
                    samples.Add(new(index,elapsed,elapsed/PortraitControl.AnimationSpeed,phase,done,scale,angle,nativeAlpha,inspectionAlpha,MotionHash(native),MotionHash(inspection),contact));
                    nativeFrames.Add(native);inspectionFrames.Add(inspection);sampleCount++;
                }
                if(!samples[^1].Done)throw new InvalidOperationException("The final sample must include neutral completion.");
                if(!new[]{"Begin","Perform","Recover","Idle"}.All(phase=>samples.Any(s=>s.Phase==phase)))throw new InvalidOperationException("Sample coverage omits a required animation phase.");
                var prefix=character.Id+"--"+clip.Id.Replace('.','-');
                var nativePath="strips/"+prefix+"--47.png";var inspectionPath="strips/"+prefix+"--188.png";
                MotionWriteStrip(nativeFrames,MotionNativeSide,Path.Combine(directory,nativePath));MotionWriteStrip(inspectionFrames,MotionLogicalSide,Path.Combine(directory,inspectionPath));
                var entry=new MotionEntry(character.Id,character.Name,clip.Id,clip.Name,clip.Category,clip.Weapon,clip.Duration,clip.Duration/PortraitControl.AnimationSpeed,
                    nativePath,inspectionPath,samples.Select(s=>s.NativeHash).Distinct().Count(),samples.Select(s=>s.InspectionHash).Distinct().Count(),samples);
                entries.Add(entry);
                if(samples.Any(s=>s.InspectionAlpha.TopAndSideEdgePixels>0))warnings.Add(new{characterId=character.Id,clipId=clip.Id,kind="top-or-side-edge",frames=samples.Where(s=>s.InspectionAlpha.TopAndSideEdgePixels>0).Select(s=>s.Index).ToArray()});
                if(entry.DistinctInspectionFrames<2)warnings.Add(new{characterId=character.Id,clipId=clip.Id,kind="visually-constant"});
                // RenderTargetBitmap owns native WPF resources that are not reflected
                // fully in managed GC pressure. A long catalog must release each clip
                // before allocating the next; otherwise thousands of tiny frozen
                // snapshots can exhaust MIL resources despite modest managed heaps.
                nativeFrames.Clear();inspectionFrames.Clear();
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            File.WriteAllText(Path.Combine(directory,"progress.json"),JsonSerializer.Serialize(new{character=character.Id,completedCharacters=entries.Select(e=>e.CharacterId).Distinct().Count(),clips=entries.Count,sampleCount}));
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        checks.Add(coreOnly?"Early subset lists five core clips per character; unavailable scenes have zero samples and no pass credit.":"Eight characters expose 70 planned base sequences plus configured signature actions. Only available scenes are rendered; unavailable scenes have zero samples and no pass credit.");
        checks.Add("Every rendered clip has 24, 48, 72 or 144 runtime samples including begin, perform, recover and neutral completion; longer scenes receive denser sampling.");
        checks.Add("Every sample is nonblank at 47px and 188px; the 47px stage is exactly 25% of both 188px dimensions.");
        checks.Add("Basic, motion and activity banks each have 12 registered frames sharing one logical canvas size per character.");
        checks.Add("All pure sampler frames retain ActorScale=1 and ActorAngle=0; this does not by itself establish contact or likeness.");
        checks.Add("All PNGs retain transparency; no background or floor artwork is baked into a character strip.");
        var artifactFiles=entries.Where(e=>e.Available).SelectMany(e=>new[]{e.NativeStrip,e.InspectionStrip}).Distinct().Select(relative=>Path.Combine(directory,relative)).OrderBy(p=>p,StringComparer.Ordinal).Select(p=>new{path=Path.GetRelativePath(directory,p).Replace('\\','/'),bytes=new FileInfo(p).Length,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()}).ToArray();
        var sourceFiles=Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"Assets")).Where(p=>p.EndsWith(".png",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
            .OrderBy(p=>p,StringComparer.Ordinal).Select(p=>new{file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()}).ToArray();
        var report=new{rendered=true,coreOnly,targeted=onlyCharacter!=null||onlyClipIds!=null,createdUtc=DateTimeOffset.UtcNow,characters=entries.Select(e=>e.CharacterId).Distinct().Count(),plannedBaseClips=entries.Count(e=>!e.Weapon),plannedWeaponClips=entries.Count(e=>e.Weapon),
            baseClips=entries.Count(e=>!e.Weapon&&e.Available),weaponClips=entries.Count(e=>e.Weapon&&e.Available),renderedClips=entries.Count(e=>e.Available),unavailableClips=entries.Count(e=>!e.Available),allPlannedClipsRendered=entries.All(e=>e.Available),
            sampleCounts=entries.Where(e=>e.Available).Select(e=>e.SampleCount).Distinct().Order().ToArray(),sampleCount,logicalSide=MotionLogicalSide,nativeSide=MotionNativeSide,linearScale=.25,runtimeSpeed=PortraitControl.AnimationSpeed,
            checks,warnings,gaitChecks,contactEvidenceAvailable=evidenceMethod!=null,entries,sourceFiles,artifactFiles,
            gameCaptured=false,desktopCaptured=false,windowShown=false,modelCalls=0,networkRequests=0,microphoneOpened=false,audioPlayed=false,
            note="This is a finite sampled review, not smooth interpolation, new artwork, or proof of subjective quality. Edge contact is a review hint. Duo actions show one actor; paired spacing needs a separate native review."};
        var options=new JsonSerializerOptions{WriteIndented=true};
        File.WriteAllText(Path.Combine(directory,"motion-review.json"),JsonSerializer.Serialize(report,options));
        if(onlyCharacter==null&&onlyClipIds==null)MotionContactSheet(directory,entries);else MotionTargetSheet(directory,entries);
        File.WriteAllText(Path.Combine(directory,"MotionReview.html"),MotionHtml.Replace("__SCOPE_LABEL__",coreOnly?"Five planned core scenes per companion. Unavailable artwork is marked and has no playback.":"Full catalog: eight companions, 70 planned base sequences each, plus signature actions. Availability is stated per scene; a catalog entry is not visual acceptance.")
            .Replace("__REVIEW_DATA__",JsonSerializer.Serialize(new{entries,coreOnly,nativeSide=MotionNativeSide,logicalSide=MotionLogicalSide,samples=MotionSamples,speed=PortraitControl.AnimationSpeed,warnings,contactEvidenceAvailable=evidenceMethod!=null})));
        if(!coreOnly&&onlyCharacter==null&&onlyClipIds==null)WriteReviewHub(Path.GetDirectoryName(Path.GetFullPath(directory))!,"Updated frames were generated. Animation testing remains on hold until all required complete-motion and frame-by-frame checks pass; generation alone is not approval. Voice listening remains manual.");
    }

    private static void MotionLayout(FrameworkElement element)
    {element.Measure(new Size(MotionLogicalSide,MotionLogicalSide));element.Arrange(new Rect(0,0,MotionLogicalSide,MotionLogicalSide));element.UpdateLayout();}
    private static void MotionTargetSheet(string directory,IReadOnlyList<MotionEntry> entries)
    {
        const int columns=8,cell=188,pad=12,rowHeight=232,header=54;
        var rendered=entries.Where(e=>e.Available).ToArray();if(rendered.Length==0)return;
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18,23,31)),null,new Rect(0,0,columns*cell+pad*2,header+rowHeight*rendered.Length));
            DrawText(dc,"Targeted repair candidates · source contact and motion remain review pending",pad,13,18,Brushes.White);
            for(int row=0;row<rendered.Length;row++)
            {
                var entry=rendered[row];DrawText(dc,entry.Character+" / "+entry.Name,pad,header+row*rowHeight,13,Brushes.LightSlateGray);
                var strip=new BitmapImage(new Uri(Path.GetFullPath(Path.Combine(directory,entry.InspectionStrip))));
                for(int column=0;column<columns;column++)
                {
                    int index=(int)Math.Round((entry.SampleCount-1)*column/(double)(columns-1));
                    var frame=new CroppedBitmap(strip,new Int32Rect(index%entry.SheetColumns*cell,index/entry.SheetColumns*cell,cell,cell));
                    double x=pad+column*cell,y=header+row*rowHeight+22;dc.DrawImage(frame,new Rect(x,y,cell,cell));
                    dc.DrawLine(new Pen(Brushes.OliveDrab,1),new Point(x,y+cell),new Point(x+cell,y+cell));
                    DrawText(dc,$"frame{index} · {entry.Samples[index].RealSeconds:0.00}s",x,y+cell+3,10,Brushes.LightSlateGray);
                }
            }
        }
        var bitmap=new RenderTargetBitmap(columns*cell+pad*2,header+rowHeight*rendered.Length,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);SaveBitmap(bitmap,Path.Combine(directory,"TargetedReview.png"));
    }
    private static RenderTargetBitmap MotionSnapshot(FrameworkElement element,int side)
    {
        var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        using(var dc=visual.RenderOpen())dc.DrawRectangle(new VisualBrush(element){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,MotionLogicalSide,MotionLogicalSide),Stretch=Stretch.Fill},null,new Rect(0,0,side,side));
        var bitmap=new RenderTargetBitmap(side,side,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    private static byte[] MotionPixels(BitmapSource bitmap){var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return pixels;}
    private static string MotionHash(BitmapSource bitmap)=>Convert.ToHexString(SHA256.HashData(MotionPixels(bitmap))).ToLowerInvariant();
    private static MotionAlpha MotionAlphaBounds(BitmapSource bitmap)
    {
        var pixels=MotionPixels(bitmap);int count=0,left=bitmap.PixelWidth,top=bitmap.PixelHeight,right=-1,bottom=-1,edge=0,floor=0;
        for(int y=0;y<bitmap.PixelHeight;y++)for(int x=0;x<bitmap.PixelWidth;x++)
        {
            if(pixels[(y*bitmap.PixelWidth+x)*4+3]<32)continue;
            count++;left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
            if(x==0||y==0||x==bitmap.PixelWidth-1)edge++;if(y==bitmap.PixelHeight-1)floor++;
        }
        return new(count,left,top,right,bottom,edge,floor);
    }
    private static void MotionWriteStrip(IReadOnlyList<BitmapSource> frames,int side,string path)
    {
        int columns=Math.Min(MotionSamples,frames.Count),rows=(frames.Count+columns-1)/columns,width=side*columns,height=side*rows;
        var pixels=new byte[width*height*4];int stride=width*4;
        for(int frame=0;frame<frames.Count;frame++)
        {
            var source=MotionPixels(frames[frame]);for(int y=0;y<side;y++)Buffer.BlockCopy(source,y*side*4,pixels,(frame/columns*side+y)*stride+frame%columns*side*4,side*4);
        }
        var strip=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,pixels,stride);SaveBitmap(strip,path);
    }
    private static void MotionContactSheet(string directory,IReadOnlyList<MotionEntry> entries)
    {
        const int columnWidth=206,rowHeight=242,left=16,header=80;string[] ids=["furniture.bed","furniture.table-chair","furniture.couch-tv"];
        bool coreOnly=!entries.Any(e=>e.Id=="furniture.couch-tv");
        int columns=coreOnly?5:6,width=left*2+columns*columnWidth,height=header+Characters.All.Length*rowHeight;
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20,25,33)),null,new Rect(0,0,width,height));
            DrawText(dc,"Motion contact review · actual runtime samples",left,12,23,Brushes.White);
            DrawText(dc,"188px inspection stages; 47px inset shows actual desktop size. Playback and exact frame evidence are in MotionReview.html.",left,47,12,Brushes.LightSlateGray);
            for(int row=0;row<Characters.All.Length;row++)
            {
                var character=Characters.All[row];var own=entries.Where(e=>e.CharacterId==character.Id).ToArray();
                var selected=coreOnly?own:ids.Select(id=>own.First(e=>e.Id==id)).Concat(own.Where(e=>e.Category=="pet"&&e.Id.EndsWith(".play",StringComparison.Ordinal)).Take(2))
                    .Concat(own.Where(e=>e.Category=="pet"&&e.Id.EndsWith(".settle",StringComparison.Ordinal)).Take(1)).ToArray();
                if(selected.Length!=columns)throw new InvalidOperationException("Contact sheet needs bed, chair, couch and three pet scenes.");
                DrawText(dc,character.Name,left,header+row*rowHeight,14,Brushes.White);
                for(int col=0;col<selected.Length;col++)
                {
                    var entry=selected[col];int index=entry.SampleCount/2;double x=left+col*columnWidth,y=header+row*rowHeight+23;
                    if(!entry.Available){DrawText(dc,"Artwork unavailable",x,y+75,13,Brushes.Khaki);DrawText(dc,entry.Id,x,y+MotionLogicalSide+5,10,Brushes.LightSlateGray);continue;}
                    var source=new BitmapImage(new Uri(Path.Combine(directory,entry.InspectionStrip)));var native=new BitmapImage(new Uri(Path.Combine(directory,entry.NativeStrip)));
                    var frame=new CroppedBitmap(source,new Int32Rect(index%entry.SheetColumns*MotionLogicalSide,index/entry.SheetColumns*MotionLogicalSide,MotionLogicalSide,MotionLogicalSide));
                    var small=new CroppedBitmap(native,new Int32Rect(index%entry.SheetColumns*MotionNativeSide,index/entry.SheetColumns*MotionNativeSide,MotionNativeSide,MotionNativeSide));
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30,38,48)),null,new Rect(x,y,MotionLogicalSide,MotionLogicalSide));
                    dc.DrawImage(frame,new Rect(x,y,MotionLogicalSide,MotionLogicalSide));
                    dc.DrawLine(new Pen(Brushes.OliveDrab,1),new Point(x,y+MotionLogicalSide),new Point(x+MotionLogicalSide,y+MotionLogicalSide));
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12,16,22)),null,new Rect(x+MotionLogicalSide-MotionNativeSide,y,MotionNativeSide,MotionNativeSide));
                    dc.DrawImage(small,new Rect(x+MotionLogicalSide-MotionNativeSide,y,MotionNativeSide,MotionNativeSide));
                    DrawText(dc,entry.Id,x,y+MotionLogicalSide+5,10,Brushes.LightSlateGray);
                }
            }
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);SaveBitmap(bitmap,Path.Combine(directory,"ContactReview.png"));
    }

    private const string MotionHtml="""
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Companion motion review</title>
<style>
:root{color-scheme:dark}*{box-sizing:border-box}body{background:#10151d;color:#e6ecf5;font:15px system-ui;margin:24px auto;padding:0 22px;max-width:1160px}h1{font-size:26px}p{line-height:1.5;color:#aab9ca}label{display:inline-block;margin:5px 12px 5px 0}select,button,input{font:inherit;padding:7px;border:1px solid #57667b;border-radius:5px;background:#202c3d;color:#edf4ff}button{cursor:pointer}button:disabled{opacity:.5}.controls{padding:14px;background:#19222f;border-radius:9px}.views{display:flex;align-items:flex-start;flex-wrap:wrap;gap:30px;margin:24px 0}.view{min-width:220px}.stage{display:inline-block;position:relative;vertical-align:top;background:repeating-conic-gradient(#263443 0 25%,#1a2734 0 50%) 0/12px 12px;outline:1px solid #58687b}.stage.dark{background:#11171f}.stage.light{background:#f0f1ee}.stage::after{content:'';position:absolute;left:0;right:0;bottom:-1px;height:1px;background:#83a153;pointer-events:none}canvas{display:block;image-rendering:pixelated;image-rendering:crisp-edges}#scrub{width:min(100%,680px);vertical-align:middle}#film{display:flex;gap:4px;overflow:auto;padding:10px 1px}#film button{padding:2px;flex:none;border-color:#3e5268}#film button.active{border-color:#c4df8a}#film canvas{width:47px;height:47px}pre{white-space:pre-wrap;word-break:break-word;background:#19222f;padding:12px;border-radius:6px;font-size:12px}a{color:#a8d1ff}details{margin:18px 0}img{max-width:100%;height:auto}small{color:#aab9ca}.status{min-height:24px;color:#cbdba9}input[type=checkbox]{vertical-align:middle}
#catalog{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:5px;max-height:260px;overflow:auto;padding:8px 0}#catalog button{text-align:left;font-size:13px}nav{line-height:2}#scope{font-weight:600;color:#d7e7bc}.playlist,.feedback{border:1px solid #577055;padding:12px;margin:12px 0;border-radius:6px}.playlist button{margin:4px}textarea{display:block;width:100%;font:14px system-ui;background:#111b28;color:#edf4ff;border:1px solid #57667b;border-radius:5px;padding:8px;margin:8px 0}.feedback label{display:block}.feedback p{margin:6px 0}#playlistStatus{color:#d7e7bc}</style>
<nav><a href="../ReviewHub.html">Review home</a> · <a href="../CharacterStudy.html">Character study · not installed</a> · <a href="../MotionReview/MotionReview.html">All 70 + signature animations</a> · <a href="../MotionPreview/MotionReview.html">Quick 5-scene preview</a> · <a href="../GroundPreview/GrassReview.html">Grass and floors</a> · <a href="../../Auditions/Voice-Reference-Review.html">Voices</a></nav><h1>Companion motion review</h1>
<p>__SCOPE_LABEL__ These are actual app-rendered frames. The desktop view is <strong>47 × 47 pixels</strong>—25% of both original 188-pixel dimensions. Larger views are for inspection.</p>
<p><strong>Internal work in progress—not ready for user acceptance testing.</strong> Every character and every animation requires five complete verified motion runs plus frame-by-frame inspection. Recorded visual passes remain zero. <a href="../AllAnimationReview-20261004.html">All580-scene matrix</a> · <a href="../FiveRunReviewPlan-20261004.md">Five-run gate</a>.</p>
<p style="border:1px solid #bd9358;padding:12px">Reported issues are under targeted repair: Sylvanas careful steps, bow, quiver, couch/TV, popcorn, PC desk contact, handheld grip, and female bed blanket coverage/transparency. <a href="../SylvanasFeedback-20261004.json">Current repair and approval status</a>. A numeric check or completed playlist does not accept these scenes or activate the app.</p>
<p id="scope" role="status"></p><div class="controls"><label>Companion <select id="character"></select></label><label>Category <select id="category"><option value="all">All</option></select></label><label>Clip <select id="clip"></select></label><br>
<label>Find an animation <input id="search" type="search" placeholder="bed, pet, wave, weapon…"></label><button id="showall">Show every animation</button><br>
<button id="play">Play once</button> <button id="pause">Pause</button> <button id="restart">First frame</button> <label><input id="loop" type="checkbox"> Loop</label><label>Speed <select id="speed"><option value="1">Runtime 3×</option><option value="0.333333333">Slow inspection 1×</option></select></label><br>
<div class="playlist"><button id="playall">Play ALL animations for this character</button><button id="playlistpause">Pause playlist</button><button id="playliststop">Stop playlist</button><button id="previous">Previous scene</button><button id="next">Skip to next</button><p id="playlistStatus" role="status"></p><small>Every full scene plays to its recorded duration, including signature actions. Changing character or browsing filters stops the playlist. Reaching the end does not approve or activate anything.</small></div>
<label for="scrub">Frame</label><input id="scrub" type="range" min="0" max="23" value="0"><span id="position"></span><br>
<label>Inspection <select id="zoom"><option value="188">188px logical render</option><option value="203">203px enlargement</option><option value="376">376px enlargement</option></select></label><label>Background <select id="background"><option value="checker">Transparency grid</option><option value="dark">Dark</option><option value="light">Light</option></select></label></div>
<div class="views"><section class="view"><h3>Actual desktop size</h3><div class="stage"><canvas id="native" width="47" height="47" aria-label="Actual 47-pixel desktop avatar"></canvas></div><p><small>Green line is the stage baseline.<br>The PNG itself stays transparent.</small></p></section><section class="view"><h3>Inspection view</h3><div class="stage"><canvas id="inspection" width="188" height="188" aria-label="Enlarged runtime avatar"></canvas></div></section></div>
<details open><summary id="catalogTitle">Browse animations</summary><div id="catalog"></div></details><div id="status" class="status" aria-live="polite"></div><div id="film" aria-label="Selectable sampled frames"></div>
<section class="feedback"><button id="markframe">Mark this frame</button> <button id="copyfeedback">Copy all marked issues</button><p id="feedbackStatus" role="status">Pause and mark an error. Each issue keeps its character, scene, frame and timestamp. This report stays in this page; copy it before closing. Nothing is transmitted.</p><label>Note for the most recently marked issue<textarea id="feedbackNote" rows="2" placeholder="What looks wrong at this moment?"></textarea></label><label>Accumulated issue report · selectable text<textarea id="feedbackText" rows="8" readonly placeholder="Marked issues will appear here without replacing earlier notes."></textarea></label></section>
<p>Playback selects 24, 48, 72 or 144 finite runtime samples per clip; there is no CSS movement or invented interpolation. Runtime speed is 3× virtual animation time. The final sample is neutral completion. Solo renders of duo actions do not verify paired spacing.</p>
<details><summary>Selected-frame evidence</summary><pre id="evidence"></pre></details><details><summary>What these checks establish</summary><p id="coverage"></p><p>Constant sampler scale, transparent bounds and contact measurements are engineering evidence. They do not establish subjective animation quality, likeness, or approval. Source poses may differ in apparent proportions; inspect the full motion.</p><a href="motion-review.json">Complete evidence and source hashes</a></details>
<details><summary>Bed, chair and pet contact sheet</summary><a href="ContactReview.png"><img src="ContactReview.png" loading="lazy" alt="Runtime bed chair and pet contact review for all eight companions"></a></details>
<script>
'use strict';
const review=__REVIEW_DATA__,byId=id=>document.getElementById(id);
let entry,nativeImage,inspectionImage,frame=0,playing=false,started=0,initialTime=0,currentTime=0,activeSpeed=1,loadEpoch=0,animationRequest=0,playbackEpoch=0,loading=false;
let playlist=null,playlistSerial=0,markedFrame=null,playlistNotice='';const markedIssues=[];
const images=new Map(),categoryNames={gesture:'Gestures',prop:'Objects and props',themed:'Objects and props',furniture:'Furniture and activities',pet:'Pets',duo:'Duo interactions',weapon:'Signature weapons'};
function loadImage(path){
  if(images.has(path)){const hit=images.get(path);images.delete(path);images.set(path,hit);return hit;}
  let pending=new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>resolve(image);image.onerror=()=>reject(new Error('Could not load '+path));image.src=path;});
  pending=pending.catch(error=>{if(images.get(path)===pending)images.delete(path);throw error;});
  images.set(path,pending);while(images.size>8)images.delete(images.keys().next().value);return pending;
}
for(const [id,name]of new Map(review.entries.map(e=>[e.CharacterId,e.Character])))byId('character').add(new Option(name,id));
function seconds(value){return Math.max(0,value).toFixed(2)+'s';}
function duration(value){const rounded=Math.ceil(Math.max(0,value));return Math.floor(rounded/60)+'m '+String(rounded%60).padStart(2,'0')+'s';}
function updatePlaylist(){
  const p=playlist,available=!!entry&&entry.Available!==false&&!!nativeImage;
  byId('playlistpause').disabled=!p||p.completed;
  byId('playlistpause').textContent=p&&p.paused?'Resume playlist':'Pause playlist';
  byId('playliststop').disabled=!p;byId('previous').disabled=!p||p.index===0;byId('next').disabled=!p||p.index>=p.items.length-1;
  byId('loop').disabled=!!p;byId('markframe').disabled=!available;
  if(!p){byId('playlistStatus').textContent=playlistNotice?playlistNotice+' Approval is still pending.':review.coreOnly?'This is the five-scene shortcut. Open the full catalog for a complete character playlist.':'Full-character playback includes every catalog entry, including signature actions, regardless of search or category. Approval is still pending.';return;}
  const remaining=Math.max(0,p.items[p.index].RealDuration-currentTime)+p.items.slice(p.index+1).reduce((sum,e)=>sum+e.RealDuration,0);
  const state=p.completed?'Complete — approval is still pending':p.error?'Paused on an error':p.paused?'Paused':loading?'Loading next scene':'Playing';
  byId('playlistStatus').textContent=`${state} · ${p.index+1}/${p.items.length} · ${p.items[p.index].Name} · ${seconds(currentTime)} / ${seconds(p.items[p.index].RealDuration)} · ${duration(p.completed?0:remaining/Number(byId('speed').value))} remaining at selected speed${p.error?' · '+p.error:''}`;
}
function draw(canvas,image,side,index){const context=canvas.getContext('2d'),columns=Math.floor(image.naturalWidth/side);context.clearRect(0,0,canvas.width,canvas.height);context.imageSmoothingEnabled=false;context.drawImage(image,index%columns*side,Math.floor(index/columns)*side,side,side,0,0,canvas.width,canvas.height);}
function showFrame(index,elapsed){
  if(!entry||!nativeImage||!inspectionImage)return;
  frame=Math.max(0,Math.min(entry.Samples.length-1,index));const sample=entry.Samples[frame];currentTime=elapsed===undefined?sample.RealSeconds:elapsed;
  draw(byId('native'),nativeImage,review.nativeSide,frame);draw(byId('inspection'),inspectionImage,review.logicalSide,frame);byId('scrub').value=frame;
  byId('position').textContent=` ${frame+1}/${entry.Samples.length} · ${seconds(currentTime)} / ${seconds(entry.RealDuration)} runtime · ${sample.Phase}`;
  byId('evidence').textContent=JSON.stringify(sample,null,2);byId('film').querySelectorAll('button').forEach((b,i)=>b.classList.toggle('active',i===frame));updatePlaylist();
}
function pausePlayback(){
  if(playing&&entry){const elapsed=Math.min(entry.RealDuration,initialTime+(performance.now()-started)/1000*activeSpeed);showFrame(Math.floor(elapsed/entry.RealDuration*(entry.Samples.length-1)),elapsed);}
  playing=false;playbackEpoch++;cancelAnimationFrame(animationRequest);animationRequest=0;byId('play').textContent='Play once';
}
function pause(){pausePlayback();if(playlist)playlist.paused=true;updatePlaylist();}
function stopPlaylist(message){pausePlayback();if(message)playlistNotice=message;else if(playlist)playlistNotice='Playlist stopped because selection or filters changed.';playlist=null;playlistSerial++;loadEpoch++;loading=false;updatePlaylist();}
function play(){
  if(!entry||!nativeImage||!inspectionImage||entry.Available===false||document.hidden)return;
  if(playing)return;if(currentTime>=entry.RealDuration)showFrame(0);
  initialTime=currentTime;started=performance.now();activeSpeed=Number(byId('speed').value);playing=true;const token=++playbackEpoch;
  if(playlist){playlist.paused=false;playlist.completed=false;playlist.error=null;}
  byId('play').textContent='Playing';updatePlaylist();animationRequest=requestAnimationFrame(now=>tick(now,token));
}
function tick(now,token){
  if(!playing||token!==playbackEpoch)return;
  const elapsed=initialTime+(now-started)/1000*activeSpeed;
  if(elapsed>=entry.RealDuration){
    showFrame(entry.Samples.length-1,entry.RealDuration);pausePlayback();
    if(playlist&&!playlist.paused){advancePlaylist(playlist.index+1,true);return;}
    if(byId('loop').checked){showFrame(0);play();}return;
  }
  showFrame(Math.floor(elapsed/entry.RealDuration*(entry.Samples.length-1)),elapsed);animationRequest=requestAnimationFrame(next=>tick(next,token));
}
async function load(options={}){
  pausePlayback();const epoch=++loadEpoch,target=options.entry||review.entries.find(e=>e.CharacterId===byId('character').value&&e.Id===byId('clip').value);
  entry=target;nativeImage=inspectionImage=null;currentTime=0;frame=0;loading=!!target;
  for(const id of ['native','inspection']){const canvas=byId(id);canvas.getContext('2d').clearRect(0,0,canvas.width,canvas.height);}
  byId('film').replaceChildren();byId('status').textContent='Loading rendered frames…';
  for(const id of ['play','pause','restart','scrub'])byId(id).disabled=!entry||entry.Available===false;
  updatePlaylist();
  if(!entry){loading=false;byId('status').textContent='No clips in this category for this companion.';byId('position').textContent='';updatePlaylist();return false;}
  if(entry.Available===false){loading=false;const reason=entry.UnavailableReason||'Required artwork is pending.';byId('status').textContent='Unavailable: '+reason;byId('position').textContent='No rendered frames';byId('evidence').textContent=JSON.stringify({available:false,reason,samples:0,passed:false},null,2);if(playlist){playlist.paused=true;playlist.error=reason;}updatePlaylist();return false;}
  try{
    const loaded=await Promise.all([loadImage(target.NativeStrip),loadImage(target.InspectionStrip)]);
    if(epoch!==loadEpoch||options.playlistToken&&playlist?.token!==options.playlistToken)return false;
    [nativeImage,inspectionImage]=loaded;loading=false;byId('scrub').max=target.Samples.length-1;
    for(let i=0;i<target.Samples.length;i++){const button=document.createElement('button'),canvas=document.createElement('canvas');canvas.width=canvas.height=47;draw(canvas,nativeImage,47,i);button.append(canvas);button.title=`Frame ${i+1}`;button.setAttribute('aria-label',`Seek to frame ${i+1}`);button.onclick=()=>{pause();showFrame(i);};byId('film').append(button);}
    showFrame(0);byId('status').textContent=`${target.Character} · ${target.Id} · ${target.DistinctNativeFrames} distinct desktop samples / ${target.DistinctInspectionFrames} inspection samples · ${target.Samples.length} samples (~${((target.Samples.length-1)/target.RealDuration).toFixed(1)} fps at runtime speed)`;
    byId('coverage').textContent=`${review.entries.filter(e=>e.Available!==false).length} clips rendered / ${review.entries.length} catalog entries; ${review.entries.filter(e=>e.Available===false).length} unavailable. Contact evidence ${review.contactEvidenceAvailable?'included where supplied':'not supplied'}. ${review.warnings.length} review hints. Playback completion does not approve or activate the desktop app.`;
    if(options.autoplay&&(!options.playlistToken||playlist&&!playlist.paused)&&!document.hidden)play();else updatePlaylist();return true;
  }catch(error){if(epoch!==loadEpoch)return false;loading=false;byId('status').textContent=error.message;if(playlist){playlist.paused=true;playlist.error=error.message;}updatePlaylist();return false;}
}
function selectSingle(){stopPlaylist();return load();}
function populate(noLoad=false){
  stopPlaylist();const prior=byId('clip').value,category=byId('category').value||'all',own=review.entries.filter(e=>e.CharacterId===byId('character').value);
  byId('category').replaceChildren();byId('category').add(new Option(`ALL animations (${own.length})`,'all'));
  for(const key of [...new Set(own.map(e=>e.Category))])byId('category').add(new Option(`${categoryNames[key]||key} (${own.filter(e=>e.Category===key).length})`,key));
  if(category==='all'||own.some(e=>e.Category===category))byId('category').value=category;
  const query=byId('search').value.trim().toLowerCase(),matches=own.filter(e=>(byId('category').value==='all'||e.Category===byId('category').value)&&(!query||`${e.Name} ${e.Id} ${e.Category}`.toLowerCase().includes(query)));
  byId('clip').replaceChildren();byId('catalog').replaceChildren();
  for(const key of [...new Set(matches.map(e=>e.Category))]){const group=document.createElement('optgroup');group.label=categoryNames[key]||key;for(const e of matches.filter(e=>e.Category===key)){group.append(new Option(`${e.Available===false?'Unavailable — ':''}${e.Name} · ${e.Id}`,e.Id));const button=document.createElement('button');button.textContent=e.Name+(e.Available===false?' — unavailable':'');button.title=e.UnavailableReason||e.Id;button.onclick=()=>{byId('clip').value=e.Id;selectSingle();};byId('catalog').append(button);}byId('clip').append(group);}
  if(matches.length)byId('clip').value=matches.some(e=>e.Id===prior)?prior:matches[0].Id;
  byId('scope').textContent=review.coreOnly?`QUICK PREVIEW: ${own.length} scenes for this companion. Full-character playback is available in the full catalog.`:`FULL CATALOG: ${own.filter(e=>!e.Weapon).length} base sequences + ${own.filter(e=>e.Weapon).length} signature entries · ${own.filter(e=>e.Available!==false).length} rendered / ${own.filter(e=>e.Available===false).length} unavailable.`;
  byId('catalogTitle').textContent=`Browse ${matches.length} of ${own.length} animations`;if(!noLoad)load();
}
async function startPlaylist(){
  if(review.coreOnly){location.href='../MotionReview/MotionReview.html#character='+encodeURIComponent(byId('character').value);return;}
  byId('category').value='all';byId('search').value='';populate(true);
  const items=review.entries.filter(e=>e.CharacterId===byId('character').value);if(!items.length)return;
  playlistNotice='';playlist={token:++playlistSerial,items,index:0,paused:false,completed:false,error:null};await advancePlaylist(0,true);
}
async function advancePlaylist(index,autoplay){
  const p=playlist;if(!p)return;pausePlayback();
  if(index>=p.items.length){p.completed=true;p.paused=false;currentTime=p.items[p.index].RealDuration;updatePlaylist();return;}
  if(index<0)return;p.index=index;p.completed=false;p.error=null;p.paused=!autoplay;
  byId('clip').value=p.items[index].Id;await load({entry:p.items[index],playlistToken:p.token,autoplay});
}
function resumePlaylist(){const p=playlist;if(!p)return;if(document.hidden){p.paused=true;updatePlaylist();return;}p.paused=false;p.error=null;if(nativeImage&&entry===p.items[p.index])play();else load({entry:p.items[p.index],playlistToken:p.token,autoplay:true});}
function feedbackText(){if(!markedFrame)return;markedFrame.note=byId('feedbackNote').value;byId('feedbackText').value=markedIssues.map((m,i)=>`Issue ${i+1}\nCharacter: ${m.character} (${m.characterId})\nAnimation: ${m.name} (${m.id})\nFrame: ${m.frame+1}/${m.frames}\nSample runtime: ${m.sampleSeconds.toFixed(3)}s / ${m.duration.toFixed(3)}s\nPlayback elapsed: ${m.playbackSeconds.toFixed(3)}s\nVirtual animation time: ${m.virtualSeconds.toFixed(3)}s\nNote: ${m.note}`).join('\n\n')+'\n\nReview only — approval pending; desktop app not activated.';}
function markFrame(){pause();if(!entry||!nativeImage)return;if(markedFrame){feedbackText();byId('feedbackNote').value='';}const s=entry.Samples[frame];markedFrame={character:entry.Character,characterId:entry.CharacterId,name:entry.Name,id:entry.Id,frame,frames:entry.Samples.length,sampleSeconds:s.RealSeconds,playbackSeconds:currentTime,virtualSeconds:s.VirtualSeconds??s.RealSeconds*(review.speed||3),duration:entry.RealDuration,note:byId('feedbackNote').value};markedIssues.push(markedFrame);feedbackText();byId('feedbackStatus').textContent=`Issue ${markedIssues.length} marked locally. Earlier issues remain below. Add a note, then copy the report before closing this page. Nothing is sent.`;}
async function copyFeedback(){if(!markedFrame){byId('feedbackStatus').textContent='Mark a frame first.';return;}feedbackText();try{if(!navigator.clipboard?.writeText)throw new Error('Clipboard unavailable');await navigator.clipboard.writeText(byId('feedbackText').value);byId('feedbackStatus').textContent='Copied. Nothing was transmitted.';}catch{byId('feedbackText').focus();byId('feedbackText').select();byId('feedbackStatus').textContent='Clipboard unavailable for this local page. The text is selected; press Ctrl+C to copy.';}}
byId('showall').onclick=()=>{byId('category').value='all';byId('search').value='';populate();};byId('search').oninput=()=>populate();byId('character').onchange=()=>populate();byId('category').onchange=()=>populate();byId('clip').onchange=selectSingle;
byId('play').onclick=()=>{if(playlist)resumePlaylist();else{pausePlayback();play();}};byId('pause').onclick=pause;byId('restart').onclick=()=>{pause();showFrame(0);};byId('scrub').oninput=()=>{pause();showFrame(Number(byId('scrub').value));};byId('speed').onchange=pause;
byId('playall').textContent=review.coreOnly?'Open full-character playlist':'Play ALL animations for this character';byId('playall').onclick=startPlaylist;
byId('playlistpause').onclick=()=>{if(playlist?.paused)resumePlaylist();else pause();};byId('playliststop').onclick=()=>stopPlaylist('Playlist stopped.');
byId('previous').onclick=()=>{if(playlist)advancePlaylist(playlist.index-1,!playlist.paused&&!playlist.completed);};byId('next').onclick=()=>{if(playlist)advancePlaylist(playlist.index+1,!playlist.paused&&!playlist.completed);};
byId('markframe').onclick=markFrame;byId('feedbackNote').oninput=feedbackText;byId('copyfeedback').onclick=copyFeedback;
byId('zoom').onchange=()=>{const side=byId('zoom').value;byId('inspection').style.width=side+'px';byId('inspection').style.height=side+'px';};byId('background').onchange=()=>document.querySelectorAll('.stage').forEach(stage=>stage.className='stage '+byId('background').value);
document.addEventListener('visibilitychange',()=>{if(document.hidden)pause();});
const initialCharacter=new URLSearchParams(location.hash.slice(1)).get('character');if(review.entries.some(e=>e.CharacterId===initialCharacter))byId('character').value=initialCharacter;populate();

</script></html>
""";
}
