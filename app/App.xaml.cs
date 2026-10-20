using System.Windows;
namespace AzerothCompanion;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--audio-output-check",StringComparer.Ordinal))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=System.IO.Path.GetFullPath(e.Args.SkipWhile(arg=>arg!="--audio-output-check").Skip(1).First());
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            object report;int code=0;
            try{report=Services.AudioOutputSelection.Verify();}
            catch(Exception error){report=new{allPassed=false,error=error.ToString(),speakerPlayback=false};code=1;}
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        if(e.Args.Contains("--screen-self-test",StringComparer.Ordinal))
        {
            // Captures each display once and counts OCR characters. Text and pixels are never saved or spoken.
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=System.IO.Path.GetFullPath(e.Args.SkipWhile(arg=>arg!="--screen-self-test").Skip(1).First());
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            object report;int code=0;
            try
            {
                var rows=new List<object>();var engine=new Services.OcrService();
                foreach(var display in Services.ScreenGrab.ListDisplays())
                {
                    var frame=Services.ScreenGrab.Capture(display);
                    int lit=0;for(int i=0;i<frame.BgraPixels.Length;i+=16)if(frame.BgraPixels[i]!=0||frame.BgraPixels[i+1]!=0||frame.BgraPixels[i+2]!=0)lit++;
                    var ocrResult=await engine.RecognizeAsync(frame);
                    rows.Add(new{display.Number,display.Device,display.Primary,frame.Width,frame.Height,sampledLitPixels=lit,ocrLines=ocrResult.Lines.Count,ocrCharacters=ocrResult.Text.Length});
                }
                report=new{allPassed=rows.Count>0,displays=rows,textSaved=false,audioPlayed=false};
            }
            catch(Exception error){report=new{allPassed=false,error=error.ToString()};code=1;}
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        if(e.Args.Contains("--animation-availability",StringComparer.Ordinal))
        {
            // Lists every animation clip per character and whether its artwork loads. Nothing is played or captured.
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=System.IO.Path.GetFullPath(e.Args.SkipWhile(arg=>arg!="--animation-availability").Skip(1).First());
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            object report;int code=0;
            try
            {
                var rows=new List<object>();
                foreach(var profile in Core.Characters.All)
                {
                    var portrait=new Controls.PortraitControl();portrait.SetCharacter(profile);
                    var all=portrait.AnimationAvailability;
                    rows.Add(new{profile.Id,total=all.Count,available=all.Count(a=>a.Available),
                        unavailable=all.Where(a=>!a.Available).Select(a=>new{a.Id,a.Reason}).ToArray(),
                        byKind=all.Where(a=>a.Available).GroupBy(a=>a.Id.Split('.')[0]).ToDictionary(g=>g.Key,g=>g.Count())});
                }
                report=new{allPassed=true,characters=rows};
            }
            catch(Exception error){report=new{allPassed=false,error=error.ToString()};code=1;}
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        if(e.Args.Contains("--window-read-test",StringComparer.Ordinal))
        {
            // Reads ONE named window handle and reports only whether expected words were found. Text is never saved or spoken.
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var rest=e.Args.SkipWhile(arg=>arg!="--window-read-test").Skip(1).ToArray();
            var hwnd=(nint)long.Parse(rest[0]);var path=System.IO.Path.GetFullPath(rest[1]);var words=rest[2].Split(',');
            object report;int code=0;
            try
            {
                Services.ScreenGrab.ActiveWindow.SetForTest(hwnd);
                if(!Services.ScreenGrab.ActiveWindow.TryDescribe(out var bounds,out var title))throw new InvalidOperationException("Window not readable.");
                var frame=Services.ScreenGrab.CaptureRect(bounds,"window");var ocrResult=await new Services.OcrService().RecognizeAsync(frame);
                var lower=ocrResult.Text.ToLowerInvariant();
                report=new{allPassed=words.All(w=>lower.Contains(w.ToLowerInvariant())),title,width=frame.Width,height=frame.Height,lines=ocrResult.Lines.Count,wordsFound=words.ToDictionary(w=>w,w=>lower.Contains(w.ToLowerInvariant())),textSaved=false};
            }
            catch(Exception error){report=new{allPassed=false,error=error.ToString()};code=1;}
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        if(e.Args.Contains("--narration-render-test",StringComparer.Ordinal))
        {
            // Asks the local voice service to render a short phrase per voice and measures it. Nothing is played or saved.
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var rest=e.Args.SkipWhile(arg=>arg!="--narration-render-test").Skip(1).ToArray();
            var path=System.IO.Path.GetFullPath(rest[0]);var voices=rest[1].Split(',');
            object report;int code=0;
            try
            {
                var client=new Services.NarrationClient();var rows=new List<object>();
                foreach(var voice in voices)
                {
                    var timer=System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var audio=await client.RenderAsync("The road ahead is long, but we will walk it together.",voice);
                        double seconds=audio==null?0:audio.PcmFloat32.Length/4d/audio.SampleRate;
                        double peak=0,sum=0;int n=0;if(audio!=null)for(int i=0;i<audio.PcmFloat32.Length;i+=4){var v=Math.Abs(BitConverter.ToSingle(audio.PcmFloat32,i));peak=Math.Max(peak,v);sum+=v*v;n++;}
                        double rms=n>0?Math.Sqrt(sum/n):0;
                        rows.Add(new{voice,rendered=audio!=null,audioSeconds=Math.Round(seconds,2),peak=Math.Round(peak,3),rms=Math.Round(rms,4),rmsDb=Math.Round(20*Math.Log10(Math.Max(rms,1e-6)),1),renderSeconds=Math.Round(timer.Elapsed.TotalSeconds,1)});
                    }
                    catch(Exception error){rows.Add(new{voice,rendered=false,error=error.Message});}
                }
                report=new{allPassed=rows.All(r=>r.ToString()!.Contains("rendered = True")),voices=rows,audioPlayed=false,audioSaved=false};
            }
            catch(Exception error){report=new{allPassed=false,error=error.ToString()};code=1;}
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        if(e.Args.Contains("--polish-test",StringComparer.Ordinal))
        {
            // Runs the three text-polish modes against the local model on a built-in sample. No windows, keys or clipboard are touched.
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=System.IO.Path.GetFullPath(e.Args.SkipWhile(arg=>arg!="--polish-test").Skip(1).First());
            object report;int code=0;
            try
            {
                const string sample="hey so i dont no what to doo about the the meeting tomorow, can u help me figure out what to say to my boss its kinda urgent. need: slides done, budget numbers, who is presenting";
                var service=new Services.TextPolishService();var rows=new List<object>();
                foreach(var mode in new[]{Services.PolishMode.Polish,Services.PolishMode.Prompt,Services.PolishMode.Format})
                {
                    var timer=System.Diagnostics.Stopwatch.StartNew();
                    try{using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(170));var result=await service.RunAsync(mode,sample,Services.TextPolishService.DefaultModel,cts.Token);
                        rows.Add(new{mode=mode.ToString(),ok=true,seconds=Math.Round(timer.Elapsed.TotalSeconds,1),inputChars=sample.Length,outputChars=result.Length,output=result});}
                    catch(Exception error){rows.Add(new{mode=mode.ToString(),ok=false,error=error.Message});}
                }
                var tooLong=false;try{await service.RunAsync(Services.PolishMode.Polish,new string('x',1201),Services.TextPolishService.DefaultModel,CancellationToken.None);}catch(InvalidOperationException){tooLong=true;}
                report=new{allPassed=rows.All(r=>r.ToString()!.Contains("ok = True"))&&tooLong,limitRejected1201=tooLong,modes=rows};
            }
            catch(Exception error){report=new{allPassed=false,error=error.ToString()};code=1;}
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        if(e.Args.Contains("--export-widget-frames",StringComparer.Ordinal))
        {
            // Writes the registered idle (basic) + walk (motion) frames of every companion as PNGs for the Avatar Toolbox widget.
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Core.FunctionalTestMode.Enable();
            var dir=System.IO.Path.GetFullPath(e.Args.SkipWhile(arg=>arg!="--export-widget-frames").Skip(1).First());
            System.IO.Directory.CreateDirectory(dir);var names=new List<string>();int exportCode=0;
            try
            {
                foreach(var profile in Core.Characters.All)
                {
                    foreach(var (bank,count) in new[]{("basic",12),("motion",6)})
                    {
                        var frames=Controls.SpriteAtlas.Character(profile.Id,bank);
                        for(int i=0;i<Math.Min(count,frames.Length);i++)
                        {
                            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frames[i]));
                            using var stream=System.IO.File.Create(System.IO.Path.Combine(dir,$"{profile.Id}__{bank}__{i:00}.png"));encoder.Save(stream);
                        }
                    }
                    names.Add($"{profile.Id}|{profile.Name}");
                }
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir,"characters.txt"),names);
            }
            catch(Exception error){System.IO.File.WriteAllText(System.IO.Path.Combine(dir,"export-error.txt"),error.ToString());exportCode=1;}
            Shutdown(exportCode);return;
        }
        if(e.Args.Contains("--ground-self-test",StringComparer.Ordinal))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Core.FunctionalTestMode.Enable();
            var directory=System.IO.Path.GetFullPath(e.Args.SkipWhile(arg=>arg!="--ground-self-test").Skip(1).First());
            System.IO.Directory.CreateDirectory(directory);
            try{await VisualPreview.RenderGroundReviewAsync(directory);Shutdown();}
            catch(Exception error){System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"ground-self-test-error.json"),
                System.Text.Json.JsonSerializer.Serialize(new{rendered=false,error=error.ToString()}));Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--self-test",StringComparer.Ordinal))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Core.FunctionalTestMode.Enable();
            var code=await SelfTests.RunAsync();Shutdown(code);return;
        }
        if(e.Args.Contains("--catalog-self-test",StringComparer.Ordinal)||e.Args.Contains("--catalog-live-check",StringComparer.Ordinal))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Core.FunctionalTestMode.Enable();
            var live=e.Args.Contains("--catalog-live-check",StringComparer.Ordinal);
            var flag=live?"--catalog-live-check":"--catalog-self-test";
            var path=System.IO.Path.GetFullPath(e.Args.SkipWhile(arg=>arg!=flag).Skip(1).FirstOrDefault()??
                System.IO.Path.Combine(Environment.CurrentDirectory,"work","catalog-deployment-20261004","companion-validation.json"));
            object report;int code=0;
            try{report=live?await CatalogSelfTests.ReadLiveCatalogAsync():await CatalogSelfTests.RunAsync();}
            catch(Exception error){report=new{allPassed=false,error=error.ToString(),mockTransportOnly=!live,speakerPlayback=false};code=1;}
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        var startupArgs=Core.FunctionalTestMode.StartupArguments(e.Args,Core.FunctionalTestMode.BuildRequiresFunctionalMode);
        if(startupArgs.Contains("--functional-test",StringComparer.Ordinal))
        {
            Core.FunctionalTestMode.Enable();
            var functionalMain=new MainWindow();MainWindow=functionalMain;
            functionalMain.ConfigureFunctionalTest();functionalMain.Show();
            if(!FunctionalTestOptions.TryParse(startupArgs,out var target,out var error))
            {functionalMain.ReportLiveStartupFailure(error,null);return;}
            if(target!=null)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
                await functionalMain.StartAuthorizedLiveSessionAsync(target);
            }
            return;
        }
        if(e.Args.Contains("--interaction-self-test"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;var directory=System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory,"work","interaction-checks"));System.IO.Directory.CreateDirectory(directory);
            object report;int code=0;
            try{report=await InteractionSelfTests.RunAsync(System.IO.Path.Combine(directory,"controls-preview.png"));}
            catch(Exception ex){report=new{allPassed=false,error=ex.ToString()};code=1;}
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"interaction-checks.json"),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        DispatcherUnhandledException += (_,args) => { MessageBox.Show(args.Exception.Message,"Companion error",MessageBoxButton.OK,MessageBoxImage.Error); args.Handled=true; };
        if(e.Args.Any(arg=>arg.StartsWith("--live-",StringComparison.Ordinal)))
        {
            var liveMain=new MainWindow{ShowActivated=false};MainWindow=liveMain;liveMain.Show();
            if(!LiveSessionOptions.TryParse(e.Args,out var options,out var receiptPath,out var error))
            {
                liveMain.ReportLiveStartupFailure(error,receiptPath);return;
            }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            await liveMain.StartAuthorizedLiveSessionAsync(options!);return;
        }
        if(e.Args.Contains("--render-overlay"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=e.Args.SkipWhile(x=>x!="--render-overlay").Skip(1).First();
            await VisualPreview.RenderOverlayAsync(System.IO.Path.GetFullPath(path));Shutdown();return;
        }
        if(e.Args.Contains("--render-gallery"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=e.Args.SkipWhile(x=>x!="--render-gallery").Skip(1).First();
            await VisualPreview.RenderGalleryAsync(System.IO.Path.GetFullPath(path));Shutdown();return;
        }
        if(e.Args.Contains("--render-animation-review"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var path=e.Args.SkipWhile(x=>x!="--render-animation-review").Skip(1).First();
            try{await VisualPreview.RenderAnimationReviewAsync(System.IO.Path.GetFullPath(path));Shutdown();}
            catch(Exception ex){System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(path),"error.json"),System.Text.Json.JsonSerializer.Serialize(new{rendered=false,error=ex.ToString()}));Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--render-motion-review"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var directory=System.IO.Path.GetFullPath(e.Args.SkipWhile(x=>x!="--render-motion-review").Skip(1).First());
            System.IO.Directory.CreateDirectory(directory);
            try{await VisualPreview.RenderMotionReviewAsync(directory);Shutdown();}
            catch(Exception ex){System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"motion-review-error.json"),System.Text.Json.JsonSerializer.Serialize(new{rendered=false,error=ex.ToString()}));Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--ui-self-test"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;int code=0;
            object report;
            try{report=new{passed=await UiSelfTests.RunAsync(),allPassed=true,paidApiCalled=false,audioPlayed=false};}
            catch(Exception ex){report=new{error=ex.ToString(),allPassed=false};code=1;}
            var folder=System.IO.Path.Combine(AppContext.BaseDirectory,"Verification");System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder,"ui-self-tests.json"),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Shutdown(code);return;
        }
        if(e.Args.Contains("--self-test"))
        {
            var code=await SelfTests.RunAsync(); Shutdown(code); return;
        }
        var main = new MainWindow(); MainWindow=main; main.Show();
        if(e.Args.Contains("--render-preview"))
        {
            await Task.Delay(1700);
            var path = e.Args.SkipWhile(x=>x!="--render-preview").Skip(1).FirstOrDefault() ?? "preview.png";
            main.SavePreview(System.IO.Path.GetFullPath(path));
            main.Close();
        }
    }
}
