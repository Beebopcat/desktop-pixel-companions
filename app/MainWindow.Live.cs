using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;

// These options are accepted only by the explicit live-test command-line path.
// No preference, environment variable, or normal startup can enter this path.
internal sealed record LiveSessionOptions(uint ProcessId,string ProcessName,bool LorathSpeech,string ReceiptPath,bool AssignedSpeech=false,bool FunctionalTest=false)
{
    private static readonly string[] Flags=["--live-pid","--live-process","--live-voice","--live-receipt"];

    internal static bool TryParse(string[] args,out LiveSessionOptions? options,out string? receiptPath,out string error)
    {
        options=null;receiptPath=null;error="";
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        bool duplicateReceipt=false;
        for(int i=0;i<args.Length;i++)
        {
            var flag=args[i];
            if(!Flags.Contains(flag,StringComparer.Ordinal))
            {
                if(error.Length==0)error="Live startup accepts only --live-pid, --live-process, --live-voice, and --live-receipt.";
                continue;
            }
            if(i+1>=args.Length||args[i+1].StartsWith("--",StringComparison.Ordinal))
            {
                if(error.Length==0)error="Missing value for "+flag+".";
                continue;
            }
            var value=args[++i];
            if(!values.TryAdd(flag,value))
            {
                if(flag=="--live-receipt")duplicateReceipt=true;
                if(error.Length==0)error="Duplicate live option: "+flag+".";
            }
        }
        if(!duplicateReceipt&&values.TryGetValue("--live-receipt",out var candidatePath))
        {
            try
            {
                if(Path.IsPathFullyQualified(candidatePath)&&Path.GetExtension(candidatePath).Equals(".json",StringComparison.OrdinalIgnoreCase))
                    receiptPath=Path.GetFullPath(candidatePath);
            }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or PathTooLongException){ }
        }
        if(error.Length>0)return false;
        foreach(var flag in Flags)
            if(!values.ContainsKey(flag)){error="Missing required live option: "+flag+".";return false;}
        if(!uint.TryParse(values["--live-pid"],out var pid)||pid==0)
        {error="--live-pid must be a positive process ID.";return false;}
        var processName=values["--live-process"];
        if(string.IsNullOrWhiteSpace(processName)||processName.Length>200||processName!=processName.Trim()||processName.IndexOfAny(['\\','/'])>=0)
        {error="--live-process must be the exact process name, without a path or surrounding spaces.";return false;}
        var voice=values["--live-voice"];
        if(voice!="lorath"&&voice!="off"&&voice!="assigned")
        {error="--live-voice must be assigned, lorath or off.";return false;}
        if(receiptPath==null)
        {error="--live-receipt must be an absolute path ending in .json.";return false;}
        options=new(pid,processName,voice=="lorath",receiptPath,voice=="assigned");return true;
    }
}

public partial class MainWindow
{
    private LiveSessionOptions? liveOptions;
    private DispatcherTimer? liveHeartbeat;
    private DateTimeOffset? liveStartedUtc;
    private bool liveStartupSucceeded;
    private string? liveFailureReason;
    private int liveInitialWidth,liveInitialHeight,liveInitialTextLength;

    internal async Task StartAuthorizedLiveSessionAsync(LiveSessionOptions options)
    {
        liveOptions=options;liveStartedUtc=DateTimeOffset.UtcNow;
        try
        {
            // Prove the metadata destination is writable before enabling capture.
            WriteLiveReceipt("starting");
            ProcessingPicker.SelectedIndex=0;IncludeImageCheck.IsChecked=false;
            CloudConsentCheck.IsChecked=false;ResearchCheck.IsChecked=false;
            QuestResearchCheck.IsChecked=false;QuestionResearchCheck.IsChecked=false;
            AutoReadCheck.IsChecked=false;BanterCheck.IsChecked=false;BusyCheck.IsChecked=true;
            SpeechCheck.IsChecked=false;SpoilersCheck.IsChecked=false;
            DuoCheck.IsChecked=preferences.Duo;SelectCharacter(Characters.Find(preferences.CharacterId));
            RoiX.Text="0";RoiY.Text="0";RoiW.Text="100";RoiH.Text="100";

            var windows=capture.ListWindows();WindowPicker.ItemsSource=windows;
            var matches=windows.Where(window=>window.ProcessId==options.ProcessId&&
                window.ProcessName.Equals(options.ProcessName,StringComparison.OrdinalIgnoreCase)&&
                CaptureNative.IsWindow(window.Handle)&&!CaptureNative.IsIconic(window.Handle)).ToArray();
            if(matches.Length!=1)
                throw new InvalidOperationException(matches.Length==0?
                    "The requested PID and exact process name have no visible capturable window.":
                    "The requested process has multiple visible windows. Live startup requires exactly one.");
            WindowPicker.SelectedItem=matches[0];
            await StartSelectedWindowAsync(matches[0]);
            if(!policy.Running||!capture.IsCapturing)
                throw new InvalidOperationException(StatusText.Text);

            // Establish one frame and OCR result silently; neither speech nor inference is invoked.
            await ObserveAsync(turnCts.Token,true);
            if(!policy.Running||!capture.IsCapturing||latestFrame==null)
                throw new InvalidOperationException("The selected window stopped before the initial observation completed.");
            liveInitialWidth=latestFrame.Width;liveInitialHeight=latestFrame.Height;
            liveInitialTextLength=latestText.Length;

            SpeechCheck.IsChecked=options.LorathSpeech||options.AssignedSpeech;
            if(options.LorathSpeech){preferences.CharacterVoices[character.Id]=NarrationClient.ApprovedProfile;preferences.CharacterVoices[Partner.Id]=NarrationClient.ApprovedProfile;UpdateVoicePickers();}
            var caption=options.FunctionalTest?"Static companions · animation review pending · speech and mic start off.":options.AssignedSpeech?"Pixel companions · Open mic to talk. Open to choose voices.":options.LorathSpeech?
                "Lorath B test · Open mic to talk. Choose voices with Open.":
                "Muted test · Open mic to talk. Choose voices with Open.";
            ReplyText.Text=caption;SetVisual("idle",caption);
            PopOut_Click(this,new RoutedEventArgs());overlay?.SetState("idle",caption);
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Loaded);
            if(!policy.Running||!capture.IsCapturing)
                throw new InvalidOperationException("The selected window stopped before live startup completed.");
            Status(options.FunctionalTest?"Functional test ready · selected-window capture · static companions · speech and mic off until enabled.":options.AssignedSpeech?"Live test ready · selected-window capture · assigned character voices · Open mic is off until clicked.":options.LorathSpeech?
                "Live test ready · selected-window capture · temporary Lorath B · manual interaction only.":
                "Live test ready · selected-window capture · muted · manual interaction only.");
            liveStartupSucceeded=true;WriteLiveReceipt("ready");
            liveHeartbeat=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
            liveHeartbeat.Tick+=(_,_)=>WriteLiveReceiptSafely(LiveState());
            liveHeartbeat.Start();
            Closed+=(_,_)=>{liveHeartbeat?.Stop();WriteLiveReceiptSafely("closed");};
            if(!options.FunctionalTest)WindowState=WindowState.Minimized;
        }
        catch(Exception ex)
        {
            liveStartupSucceeded=false;liveFailureReason=ex.Message;
            liveHeartbeat?.Stop();StopSession("Live startup stopped: "+ex.Message);
            ReplyText.Text="Live startup stopped. "+ex.Message;overlay?.SetState("paused","Live startup stopped · capture and speech off.");
            WindowState=WindowState.Normal;WriteLiveReceiptSafely("failed");
        }
    }

    internal void ReportLiveStartupFailure(string reason,string? receiptPath)
    {
        liveFailureReason=reason;
        if(receiptPath!=null)liveOptions=new(0,"",false,receiptPath);
        Status("Live startup stopped: "+reason);ReplyText.Text="Live startup stopped. "+reason;
        WriteLiveReceiptSafely("invalid-options");
    }

    private string LiveState()=>closed?"closed":!policy.Running?"stopped":
        microphone?.IsListening==true?"listening":responding?"responding":
        audio.IsPlaying?"speaking":policy.Quiet?"quiet":"observing";

    private object? LiveGeometry()
    {
        if(overlay?.IsLoaded!=true)return null;
        var source=JsonSerializer.SerializeToElement(overlay.GeometryEvidence());
        // GeometryEvidence also has fixture-specific assertions; copy only dimensions.
        var result=new Dictionary<string,JsonElement>();
        foreach(var name in new[]{"widthPixels","heightPixels","monitorPixels","areaPercent","withinCap","duo"})
            if(source.TryGetProperty(name,out var value))result[name]=value.Clone();
        return result;
    }

    private void WriteLiveReceipt(string state)
    {
        if(liveOptions==null)return;
        var receipt=new
        {
            schemaVersion=1,mode=liveOptions.FunctionalTest?"functional-test-only":"authorized-live-test",timeUtc=DateTimeOffset.UtcNow,
            animationsInactive=FunctionalTestMode.Active,animationReviewGateChanged=false,
            startedUtc=liveStartedUtc,appPid=Environment.ProcessId,
            pid=liveOptions.ProcessId,processName=liveOptions.ProcessName,state,
            startupSucceeded=liveStartupSucceeded,reason=liveFailureReason,
            capture=policy.Running&&capture.IsCapturing,
            frameWidth=latestFrame?.Width??0,frameHeight=latestFrame?.Height??0,
            frameCapturedUtc=latestFrame?.CapturedAt,
            initialFrameWidth=liveInitialWidth,initialFrameHeight=liveInitialHeight,
            initialOcrTextLength=liveInitialTextLength,
            textLength=latestText.Length,replyLength=ReplyText.Text.Length,
            responding,audioPlaying=audio.IsPlaying,speech=SpeechCheck.IsChecked==true,
            voice=SpeechCheck.IsChecked==true?VoiceFor(activeSpeaker??character.Id):"off",
            characterVoices=preferences.CharacterVoices,openMicEnabled=OpenMicCheck.IsChecked==true,
            speechPending=speechOperations>0,
            quiet=policy.Quiet,recording=false,microphoneListening=microphone?.IsListening==true,
            processing="local-cpu",paidServicesAuthorized=PaidServicePolicy.IsAuthorized,
            imageIncluded=IncludeImageCheck.IsChecked==true,
            researchEnabled=ResearchCheck.IsChecked==true,
            autoRead=AutoReadCheck.IsChecked==true,banter=BanterCheck.IsChecked==true,
            status=StatusText.Text,geometry=LiveGeometry(),
            screenshotsSaved=false,conversationSaved=false,audioSaved=false
        };
        Directory.CreateDirectory(Path.GetDirectoryName(liveOptions.ReceiptPath)!);
        var temporaryPath=liveOptions.ReceiptPath+".tmp";
        File.WriteAllText(temporaryPath,JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temporaryPath,liveOptions.ReceiptPath,true);
    }

    private void WriteLiveReceiptSafely(string state)
    {
        try{WriteLiveReceipt(state);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        {if(!closed)Status("Live metadata receipt could not be written: "+ex.Message);}
    }
}
