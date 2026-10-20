using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AzerothCompanion.Core;

namespace AzerothCompanion;

public static partial class VisualPreview
{
    internal static async Task RenderGroundReviewAsync(string directory)
    {
        Directory.CreateDirectory(directory);var frames=new List<object>();
        var before=GroundForeground();var overlay=new OverlayWindow();var focusSamples=new List<object>();
        void TrackFocus(string phase)
        {var handle=GroundForeground();GroundForegroundOwner(handle,out var process);focusSamples.Add(new{phase,handle=handle.ToInt64(),process,fixtureProcess=Environment.ProcessId,isFixture=process==Environment.ProcessId});}
        TrackFocus("before-show");
        try
        {
            overlay.SetProfile(Characters.All[0]);overlay.SetPartner(Characters.Find("jaina"),true);
            overlay.ConfigureFootprint(0,.04);overlay.SetAvatarScale(.25);overlay.SetClickThrough(true);overlay.SetState("quiet");overlay.Show();
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            TrackFocus("after-show");
            foreach(var style in new[]{"Grass","None","Stone","Wood","Snow","Arcane"})
            {
                overlay.SetFlooring(style);overlay.SetState("quiet");overlay.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                TrackFocus(style);
                var geometry=JsonSerializer.SerializeToElement(overlay.GeometryEvidence());
                var monitor=geometry.GetProperty("primaryBounds");int left=monitor.GetProperty("Left").GetInt32(),top=monitor.GetProperty("Top").GetInt32();
                int width=monitor.GetProperty("Width").GetInt32(),height=monitor.GetProperty("Height").GetInt32();
                if(!geometry.GetProperty("withinPrimaryWorkArea").GetBoolean()||!geometry.GetProperty("withinCap").GetBoolean())throw new InvalidOperationException("Ground fixture escaped its primary work area or area cap.");
                var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
                {
                    if(overlay.FloorVisual is {} floor)
                    {
                        floor.UpdateLayout();var bounds=geometry.GetProperty("floor").GetProperty("bounds");
                        dc.DrawRectangle(GroundBrush(floor),null,new Rect(bounds.GetProperty("Left").GetInt32()-left,bounds.GetProperty("Top").GetInt32()-top,bounds.GetProperty("Width").GetInt32(),bounds.GetProperty("Height").GetInt32()));
                    }
                    var boundsArray=geometry.GetProperty("individualBounds").EnumerateArray().ToArray();var portraits=overlay.AvatarVisuals;
                    for(int i=0;i<portraits.Count;i++)
                    {
                        portraits[i].UpdateLayout();var b=boundsArray[i];
                        if(!b.GetProperty("noActivate").GetBoolean()||!b.GetProperty("clickThrough").GetBoolean())throw new InvalidOperationException("Ground fixture must remain nonactivating and click-through.");
                        dc.DrawRectangle(GroundBrush(portraits[i]),null,new Rect(b.GetProperty("left").GetInt32()-left,b.GetProperty("top").GetInt32()-top,b.GetProperty("width").GetInt32(),b.GetProperty("height").GetInt32()));
                    }
                }
                var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
                string prefix=style.ToLowerInvariant(),full=prefix+"-primary.png",bottom=prefix+"-bottom.png";
                SaveBitmap(bitmap,Path.Combine(directory,full));
                int cropHeight=Math.Min(height,128);SaveBitmap(new CroppedBitmap(bitmap,new Int32Rect(0,height-cropHeight,width,cropHeight)),Path.Combine(directory,bottom));
                frames.Add(new{style,full,bottom,cropHeight,geometry});
            }
            TrackFocus("after-floor-captures");
            File.WriteAllText(Path.Combine(directory,"ground-focus-diagnostic.json"),JsonSerializer.Serialize(new{before=before.ToInt64(),after=GroundForeground().ToInt64(),fixtureProcess=Environment.ProcessId,focusSamples},new JsonSerializerOptions{WriteIndented=true}));
            if(GroundForeground()!=before)throw new InvalidOperationException("Foreground handle changed during ground fixture; inspect ground-focus-diagnostic.json for ownership.");
        }
        finally{overlay.Close();}
        File.WriteAllText(Path.Combine(directory,"ground-review.json"),JsonSerializer.Serialize(new{rendered=true,createdUtc=DateTimeOffset.UtcNow,frames,
            desktopCaptured=false,gameCaptured=false,onlyAppOwnedVisuals=true,foregroundUnchanged=true,audioPlayed=false,microphoneOpened=false,modelCalls=0,networkRequests=0,
            note="The transparent images composite only our own avatar and flooring visuals at measured primary-monitor positions. Empty space does not depict the user's wallpaper, apps or taskbar."},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(directory,"GrassReview.html"),GroundHtml.Replace("__FLOORS__",JsonSerializer.Serialize(frames)));
    }
    private static VisualBrush GroundBrush(FrameworkElement element)=>new(element){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,element.ActualWidth,element.ActualHeight),Stretch=Stretch.Fill};
    [DllImport("user32.dll",EntryPoint="GetForegroundWindow")]private static extern nint GroundForeground();
    [DllImport("user32.dll",EntryPoint="GetWindowThreadProcessId")]private static extern uint GroundForegroundOwner(nint window,out uint processId);

    private const string GroundHtml="""
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Grass and desktop floor review</title>
<style>:root{color-scheme:dark}body{background:#10151d;color:#e7eef8;font:16px system-ui;max-width:1120px;margin:24px auto;padding:0 22px}p{line-height:1.5;color:#b6c4d6}a{color:#acd3ff}nav{line-height:2}select{font:inherit;background:#243144;color:white;padding:8px;margin:6px;border:1px solid #64758a;border-radius:5px}.pane{overflow:auto;border:1px solid #4e617b;background:#172331;margin:16px 0}img{display:block;image-rendering:pixelated}.fit img{width:100%;height:auto}.native img{max-width:none}#summary{color:#d7e7bc;font-weight:600}pre{white-space:pre-wrap;word-break:break-word;font-size:12px}h2{font-size:20px}.label{padding:8px;background:#080d15;font-size:13px;color:#bcc9d8}</style>
<nav><a href="../ReviewHub.html">Review home</a> · <a href="../MotionReview/MotionReview.html">All animations</a> · <a href="../../Auditions/Voice-Reference-Review.html">Voices</a></nav>
<h1>Grass, floors and primary-monitor placement</h1><p>Choose a floor to inspect the actual app-drawn strip. The images contain only companion and floor pixels placed using Windows’ primary-monitor geometry. Wallpaper, apps and the taskbar were not captured.</p>
<label>Preview floor <select id="floor"></select></label><label>Monitor view <select id="scale"><option value="fit">Fit the full primary monitor</option><option value="native">Actual monitor pixels · scroll</option></select></label><p id="summary"></p>
<h2>Actual-size bottom edge</h2><p>This strip keeps its native pixels. Scroll sideways to inspect the full width. Empty space under the floor is the reserved taskbar area.</p><div class="pane native"><img id="bottom" alt="Actual-size primary-monitor bottom edge with companions and selected floor"><div class="label">Preview background only · the desktop overlay stays transparent.</div></div>
<h2>Full primary-monitor layout</h2><div id="monitorPane" class="pane fit"><img id="full" alt="Companion and floor placement across the primary monitor"></div><p>The browser floor selector changes this preview only; it does not change saved app settings. In the app, use the companion’s right-click Floor menu.</p>
<details><summary>Measured native geometry</summary><pre id="evidence"></pre></details><p><a href="ground-review.json">Complete geometry evidence</a></p>
<script>'use strict';const floors=__FLOORS__,el=id=>document.getElementById(id);for(const frame of floors)el('floor').add(new Option(frame.style,frame.style));function show(){const frame=floors.find(f=>f.style===el('floor').value),g=frame.geometry;el('bottom').src=frame.bottom;el('full').src=frame.full;el('summary').textContent=`${g.primaryDevice} · ${g.primaryBounds.Width} × ${g.primaryBounds.Height} primary pixels · avatar windows ${g.individualBounds.map(b=>b.width+' × '+b.height).join(' + ')} · floor surface y=${g.floor.surfaceY} · combined area ${g.areaPercent.toFixed(3)}%`;el('evidence').textContent=JSON.stringify(g,null,2);}el('floor').onchange=show;el('scale').onchange=()=>el('monitorPane').className='pane '+el('scale').value;show();</script></html>
""";
}
