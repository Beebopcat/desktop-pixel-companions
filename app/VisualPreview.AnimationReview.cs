using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AzerothCompanion.Controls;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;

public static partial class VisualPreview
{
    // These are stills rendered by the real WPF portrait control. The review never
    // screenshots another window, contacts a service, or opens microphone/audio.
    public static async Task RenderAnimationReviewAsync(string path)
    {
        const int side=203, gap=15, rowHeight=250, header=98, left=20;
        string[] scenes=["Idle","Walk pose","Character prop","Bed","Chair","Pet play","Duo wave","Signature action"];
        var width=left*2+scenes.Length*(side+gap)-gap;
        var height=header+Characters.All.Length*rowHeight+24;
        var portrait=new PortraitControl{Width=side,Height=side};portrait.Compact();
        var host=new Window{Content=portrait,Width=side,Height=side,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
            AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false};
        var cells=new List<object>();var sourceChecks=new List<object>();var warnings=new List<string>();
        var visual=new DrawingVisual();
        try
        {
            host.Show();await Dispatcher.Yield(DispatcherPriority.Loaded);
            using(var drawing=visual.RenderOpen())
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(18,23,32)),null,new Rect(0,0,width,height));
                DrawText(drawing,"Animation review · actual WPF renders",left,15,24,Brushes.White);
                DrawText(drawing,"8 characters · 203 × 203 px stages · selected stills from the runtime clips · no game capture or audio",left,49,13,Brushes.LightSlateGray);
                for(var col=0;col<scenes.Length;col++)DrawText(drawing,scenes[col],left+col*(side+gap),77,12,Brushes.LightSteelBlue);
                for(var row=0;row<Characters.All.Length;row++)
                {
                    var character=Characters.All[row];portrait.SetCharacter(character);portrait.SetState("idle");portrait.UpdateLayout();
                    var catalog=new CompanionPerformance(character.Id);
                    var pet=catalog.Definitions.First(d=>d.Id.StartsWith("pet.",StringComparison.Ordinal)&&d.Id.EndsWith(".play",StringComparison.Ordinal));
                    var prop=catalog.Definitions.FirstOrDefault(d=>d.Category=="weapon"||d.Id.StartsWith("weapon.",StringComparison.Ordinal))??catalog.Definitions.First(d=>d.Id.StartsWith("prop.",StringComparison.Ordinal));
                    var selected=new[]{catalog.Definitions.First(d=>d.Id=="gesture.breathe"),catalog.Definitions.First(d=>d.Id=="gesture.tiptoe"),prop,
                        catalog.Definitions.First(d=>d.Id=="furniture.bed"),catalog.Definitions.First(d=>d.Id=="furniture.table-chair"),pet,catalog.Definitions.First(d=>d.Id=="duo.wave")};
                    var y=header+row*rowHeight;
                    DrawText(drawing,character.Name,left,y,15,(Brush)new BrushConverter().ConvertFromString(character.Accent)!);
                    for(var col=0;col<selected.Length;col++)
                    {
                        var clip=selected[col];var elapsed=col==0?0:clip.Duration*(col==1?.43:.52);
                        if(col==0){portrait.SetState("quiet");portrait.SetState("idle");portrait.UpdateLayout();portrait.SetState("idle");}
                        else{portrait.RenderAnimationAt(clip.Id,elapsed);portrait.UpdateLayout();}
                        var bmp=new RenderTargetBitmap(side,side,96,96,PixelFormats.Pbgra32);bmp.Render(portrait);bmp.Freeze();
                        var alpha=AlphaBounds(bmp);var x=left+col*(side+gap);var top=y+24;
                        if(alpha.Count==0)throw new InvalidOperationException(character.Id+" / "+clip.Id+" rendered no visible pixels.");
                        if(col!=0&&alpha.EdgePixels>0)warnings.Add(character.Id+" / "+clip.Id+": visible pixels meet the stage edge; review the contact sheet.");
                        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(28,35,45)),new Pen(new SolidColorBrush(Color.FromRgb(51,65,83)),1),new Rect(x,top,side,side));
                        drawing.DrawImage(bmp,new Rect(x,top,side,side));
                        DrawText(drawing,col==0?"idle · grounded":clip.Id,x,top+side+4,10,Brushes.LightSlateGray);
                        cells.Add(new{characterId=character.Id,character=character.Name,scene=scenes[col],clipId=col==0?"idle":clip.Id,virtualSeconds=elapsed,x,y=top,width=side,height=side,alpha});
                    }
                    var weapon=new CompanionWeaponAnimation(character.Id).Definitions.FirstOrDefault(d=>d.Id=="weapon.magic");
                    var weaponX=left+7*(side+gap);var weaponY=y+24;
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(28,35,45)),new Pen(new SolidColorBrush(Color.FromRgb(51,65,83)),1),new Rect(weaponX,weaponY,side,side));
                    if(weapon!=null&&portrait.WeaponAnimationIds.Contains(weapon.Id))
                    {
                        var elapsed=weapon.Duration*.52;portrait.RenderAnimationAt(weapon.Id,elapsed);portrait.UpdateLayout();
                        var bmp=new RenderTargetBitmap(side,side,96,96,PixelFormats.Pbgra32);bmp.Render(portrait);var alpha=AlphaBounds(bmp);
                        if(!portrait.IsWeaponPerforming||alpha.Count==0)throw new InvalidOperationException(character.Id+" weapon review was not rendered by the weapon runtime.");
                        drawing.DrawImage(bmp,new Rect(weaponX,weaponY,side,side));DrawText(drawing,weapon.Id,weaponX,weaponY+side+4,10,Brushes.LightSlateGray);
                        cells.Add(new{characterId=character.Id,character=character.Name,scene=scenes[7],clipId=weapon.Id,virtualSeconds=elapsed,x=weaponX,y=weaponY,width=side,height=side,alpha});
                    }
                    else
                    {
                        DrawText(drawing,"No signature clip configured",weaponX+10,weaponY+90,12,Brushes.LightSlateGray);
                        cells.Add(new{characterId=character.Id,character=character.Name,scene=scenes[7],clipId="not configured",virtualSeconds=0d,x=weaponX,y=weaponY,width=side,height=side});
                    }
                    foreach(var bank in new[]{"basic","motion","activity"})
                    {
                        var frames=SpriteAtlas.Character(character.Id,bank);
                        if(frames.Length!=12)throw new InvalidOperationException(character.Id+" "+bank+" is missing its 12 runtime cells.");
                        var edged=new List<int>();
                        for(var index=0;index<frames.Length;index++)if(AlphaBounds(frames[index]).EdgePixels>0)edged.Add(index);
                        if(bank=="activity"&&character.Id is "sylvanas" or "jaina" or "rowan" or "silco"&&edged.Any(index=>index is 6 or 7))
                            throw new InvalidOperationException(character.Id+" pet-interaction source crop crosses visible neighboring artwork.");
                        sourceChecks.Add(new{characterId=character.Id,bank,frameCount=frames.Length,sourceCellsWithEdgePixels=edged});
                    }
                }
            }
            var sheet=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);sheet.Render(visual);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);SaveBitmap(sheet,path);
            var ui=await RenderControlLayoutReviewAsync(Path.Combine(Path.GetDirectoryName(path)!,"ControlsReview.png"));
            var report=new{rendered=true,characterCount=Characters.All.Length,sceneCount=scenes.Length,stagePixels=side,cells,sourceChecks,warnings,
                mainWindow=ui,gameCaptured=false,modelCalls=0,networkRequests=0,microphoneOpened=false,audioPlayed=false,
                note="Source edge pixels are review hints, not proof of truncation. These are selected runtime stills; animation quality and likeness require visual review."};
            File.WriteAllText(Path.ChangeExtension(path,"json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            WriteAnimationReviewHtml(path,cells,width,height);
        }
        finally{host.Close();}
    }

    private static async Task<object> RenderControlLayoutReviewAsync(string path)
    {
        using var http=new HttpClient(new OfflineCatalogHandler());
        var main=new MainWindow(null,new NarrationClient(http)){ShowActivated=false};
        var checks=new List<object>();
        try
        {
            main.Show();await Dispatcher.Yield(DispatcherPriority.Loaded);await Task.Delay(30);
            foreach(var size in new[]{(1250d,870d),(1050d,730d)})
            {
                main.Width=size.Item1;main.Height=size.Item2;main.UpdateLayout();
                if(main.MainPortrait.ActualHeight<175||main.CharacterScroll.ViewportHeight<150)throw new InvalidOperationException("Character portrait collapsed at "+size);
                main.CharacterScroll.ScrollToEnd();main.UpdateLayout();
                if(Math.Abs(main.CharacterScroll.VerticalOffset-main.CharacterScroll.ScrollableHeight)>=1)throw new InvalidOperationException("Character settings are clipped rather than scrollable.");
                main.CharacterScroll.ScrollToTop();main.UpdateLayout();
                var portraitBounds=main.MainPortrait.TransformToAncestor(main.CharacterScroll).TransformBounds(new Rect(0,0,main.MainPortrait.ActualWidth,main.MainPortrait.ActualHeight));
                if(portraitBounds.Bottom>main.CharacterScroll.ActualHeight+1)throw new InvalidOperationException("Main portrait feet are below the initial character viewport at "+size);
                var buttonRects=main.CharacterButtons.Children.OfType<Button>().Select(b=>b.TransformToAncestor(main).TransformBounds(new Rect(0,0,b.ActualWidth,b.ActualHeight))).ToArray();
                if(buttonRects.Length!=8||buttonRects.Any(r=>r.Width<100||r.Height<35||r.Left<0||r.Right>main.ActualWidth||r.Bottom>main.ActualHeight))throw new InvalidOperationException("Eight character choices are clipped at "+size);
                var tabs=Descendants<TabControl>(main).First();var scrollChecks=new List<object>();
                for(var index=0;index<tabs.Items.Count;index++)
                {
                    tabs.SelectedIndex=index;main.UpdateLayout();
                    var scroll=Descendants<ScrollViewer>(tabs).FirstOrDefault(s=>s.IsVisible&&s.Content is StackPanel);
                    if(scroll==null||scroll.ViewportHeight<90)throw new InvalidOperationException("A controls tab has no usable scrolling viewport.");
                    scroll.ScrollToEnd();main.UpdateLayout();
                    var reachesEnd=Math.Abs(scroll.VerticalOffset-scroll.ScrollableHeight)<1;
                    if(!reachesEnd)throw new InvalidOperationException("Controls tab cannot scroll to its final control.");
                    scrollChecks.Add(new{index,scroll.ViewportHeight,scroll.ExtentHeight,reachesEnd});scroll.ScrollToTop();
                }
                checks.Add(new{width=size.Item1,height=size.Item2,characterButtons=buttonRects.Length,scrollChecks});
                tabs.SelectedIndex=0;main.UpdateLayout();
                SaveElement(main,size.Item1==1250?path:Path.Combine(Path.GetDirectoryName(path)!,"ControlsReview-minimum.png"));
            }
            return new{allPassed=true,checks};
        }
        finally{main.Close();}
    }
    private sealed class OfflineCatalogHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if(request.Method!=HttpMethod.Get||request.RequestUri?.AbsolutePath!="/voices")throw new InvalidOperationException("Unexpected service request in the offline visual review.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true,\"voices\":[]}",Encoding.UTF8,"application/json")});
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject item) where T:DependencyObject
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(item);index++)
        {var child=VisualTreeHelper.GetChild(item,index);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}
    }
    private sealed record AlphaEvidence(int Count,int Left,int Top,int Right,int Bottom,int EdgePixels);
    private static AlphaEvidence AlphaBounds(BitmapSource source)
    {
        var bitmap=source.Format==PixelFormats.Bgra32||source.Format==PixelFormats.Pbgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
        int count=0,left=bitmap.PixelWidth,top=bitmap.PixelHeight,right=0,bottom=0,edge=0;
        for(var y=0;y<bitmap.PixelHeight;y++)for(var x=0;x<bitmap.PixelWidth;x++)
        {
            if(pixels[(y*bitmap.PixelWidth+x)*4+3]<32)continue;
            count++;left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
            if(x==0||y==0||x==bitmap.PixelWidth-1||y==bitmap.PixelHeight-1)edge++;
        }
        return new(count,left,top,right,bottom,edge);
    }
    private static void DrawText(DrawingContext drawing,string text,double x,double y,double size,Brush color)=>
        drawing.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,color,1),new Point(x,y));
    private static void SaveBitmap(BitmapSource bitmap,string path)
    {var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);}
    private static void WriteAnimationReviewHtml(string pngPath,List<object> cells,int width,int height)
    {
        var data=JsonSerializer.Serialize(cells);var png=Convert.ToBase64String(File.ReadAllBytes(pngPath));
        var html="""
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Companion animation review</title>
<style>body{background:#121720;color:#e8eef6;font:16px system-ui;margin:24px;max-width:1000px}h1{font-size:26px}p{color:#a8b7ca;line-height:1.5}select,button{font:inherit;background:#243144;color:white;border:1px solid #53657a;border-radius:6px;padding:8px;margin:4px}#stage{width:203px;height:203px;background-color:#222d3a;background-image:url(data:image/png;base64,PNGDATA);image-rendering:pixelated;transform-origin:top left;margin-top:18px;border:1px solid #46576d}#box{height:230px}code{color:#b8cce5}img{max-width:100%;height:auto}details{margin-top:24px}a{color:#abd4ff}</style>
<h1>Companion animation review</h1><p>Actual app-rendered stills at 203 × 203 pixels. Choose a companion and scene to inspect the same poses used by the desktop renderer. Full animation runs in the app; these stills do not play audio or capture anything.</p>
<label>Companion <select id="character"></select></label><label>Scene <select id="scene"></select></label><label>View <select id="zoom"><option value="1">Actual size</option><option value="2">2× pixels</option></select></label>
<div id="box"><div id="stage" role="img"></div></div><p id="caption"></p><p>In the app, right-click a companion → Choose animation. Props and scenes reuse authored sprite poses; voice candidates remain separately gated for review.</p>
<details><summary>Full contact sheet</summary><img alt="Eight-character animation contact sheet" src="data:image/png;base64,PNGDATA"></details>
<script>const cells=CELLDATA;const byId=id=>document.getElementById(id);const names=[...new Map(cells.map(c=>[c.characterId,c.character])).entries()];for(const [id,name] of names)byId('character').add(new Option(name,id));for(const scene of [...new Set(cells.map(c=>c.scene))])byId('scene').add(new Option(scene,scene));function show(){const c=cells.find(c=>c.characterId===byId('character').value&&c.scene===byId('scene').value);byId('stage').style.backgroundPosition=`-${c.x}px -${c.y}px`;byId('stage').setAttribute('aria-label',`${c.character}: ${c.scene}`);const z=Number(byId('zoom').value);byId('stage').style.transform=`scale(${z})`;byId('box').style.height=`${203*z+25}px`;byId('caption').textContent=`${c.character} · ${c.clipId} · ${c.virtualSeconds.toFixed(2)} virtual seconds`;}for(const id of ['character','scene','zoom'])byId(id).addEventListener('change',show);show();</script></html>
""";
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(pngPath)!,"AnimationReview.html"),html.Replace("PNGDATA",png).Replace("CELLDATA",data));
    }
}
