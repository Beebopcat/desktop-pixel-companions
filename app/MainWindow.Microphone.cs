using System.Windows;
using AzerothCompanion.Services;
namespace AzerothCompanion;
public partial class MainWindow
{
    private InputSpeechService? microphone;
    private Func<InputSpeechService> microphoneFactory=()=>new();
    private long microphoneGeneration,microphoneRecordingEpoch;
    private bool microphoneResumePending,pushToTalkHeld;
    private void EnsureMicrophone()
    {
        if(microphone!=null)return;
        microphone=microphoneFactory();
        microphone.TextRecognized+=(_,result)=>_ = Dispatcher.InvokeAsync(async()=>
        {
            if(!policy.Running||closed||microphoneGeneration!=generation||
                result.RecordingEpoch!=microphoneRecordingEpoch||microphone?.IsCurrentRecording(result.RecordingEpoch)!=true)return;
            QuestionBox.Text=result.Text;
            if(IsQuietCommand(result.Text)){Quiet_Click(this,new RoutedEventArgs());return;}
            Status("Local transcript ready.");
            if(SendVoiceCheck.IsChecked==true)await AskAsync(result.Text);
            else UpdateMicrophoneIndicator();
        });
        microphone.Error+=(_,error)=>_ = Dispatcher.InvokeAsync(()=>
        {
            if(closed||error.RecordingEpoch!=microphoneRecordingEpoch||microphoneGeneration!=generation)return;
            DisableOpenMicrophone();Status(error.Message);
        });
        microphone.Stopped+=(_,result)=>_ = Dispatcher.InvokeAsync(()=>
        {
            if(closed||result.RecordingEpoch!=microphoneRecordingEpoch)return;
            MicButton.Content="Hold to talk";UpdateMicrophoneIndicator();
            if(!responding&&speechOperations==0&&microphoneGeneration==generation)SetVisual(policy.Quiet?"quiet":"idle");
        });
    }
    internal static bool IsQuietCommand(string text)
    {
        var normalized=text.Trim().TrimEnd('.','!','?').ToLowerInvariant();
        return normalized is "quiet" or "be quiet" or "stop talking" or "stop speaking" or "mute" or "cancel";
    }
    private bool CanResumeOpenMicrophone()=>!closed&&policy.Running&&!policy.Quiet&&OpenMicCheck.IsChecked==true&&
        !responding&&speechOperations==0&&manualOperations==0&&!watchBusy&&!audio.IsPlaying;
    private async Task ResumeOpenMicrophoneAsync()
    {
        if(!CanResumeOpenMicrophone()||microphoneResumePending||microphone?.IsListening==true)return;
        microphoneResumePending=true;
        try
        {
            EnsureMicrophone();await microphone!.WaitForStoppedAsync();
            if(!CanResumeOpenMicrophone())return;
            MicrophoneStatusText.Text="Opening local microphone…";
            microphoneGeneration=generation;microphoneRecordingEpoch=await microphone.BeginContinuousAsync();
            if(!CanResumeOpenMicrophone()||microphoneGeneration!=generation||!microphone.IsCurrentRecording(microphoneRecordingEpoch)){microphone.Cancel();return;}
            UpdateMicrophoneIndicator();SetVisual("listening");
        }
        catch(OperationCanceledException){}
        catch(Exception ex){DisableOpenMicrophone();Status("Microphone unavailable: "+ex.Message);}
        finally{microphoneResumePending=false;}
    }
    private async void OpenMic_Changed(object sender,RoutedEventArgs e)
    {
        if(!IsInitialized||MicButton==null)return;
        if(OpenMicCheck.IsChecked==true)
        {
            if(!policy.Running){OpenMicCheck.IsChecked=false;Status("Start a selected game-window session before opening the microphone.");return;}
            if(policy.Quiet||turnCts.IsCancellationRequested)NewTurn();else policy.Quiet=false;pushToTalkHeld=false;PauseMicrophone();
            MicButton.IsEnabled=false;UpdateMicrophoneIndicator();await ResumeOpenMicrophoneAsync();
        }
        else{pushToTalkHeld=false;PauseMicrophone();MicButton.IsEnabled=true;UpdateMicrophoneIndicator();}
    }
    private void PauseMicrophone(){microphone?.Cancel();UpdateMicrophoneIndicator();}
    private void DisableOpenMicrophone()
    {
        pushToTalkHeld=false;
        if(OpenMicCheck!=null)OpenMicCheck.IsChecked=false;
        PauseMicrophone();
    }
    private void UpdateMicrophoneIndicator()
    {
        if(!IsInitialized||MicrophoneStatusText==null)return;
        bool enabled=OpenMicCheck.IsChecked==true,listening=microphone?.IsListening==true;
        MicrophoneStatusText.Text=listening?"● Listening · local · audio not saved":enabled?"Open mic paused during reply":"Mic off · local recognition";
        overlay?.SetMicrophone(enabled,listening);
    }
    partial void BeginMicrophone()
    {
        if(OpenMicCheck.IsChecked==true)return;
        pushToTalkHeld=true;_ = BeginHeldMicrophoneAsync();
    }
    private async Task BeginHeldMicrophoneAsync()
    {
        var ownerGeneration=generation;
        try
        {
            EnsureMicrophone();await microphone!.WaitForStoppedAsync();
            if(!pushToTalkHeld||!policy.Running||closed||generation!=ownerGeneration)return;
            policy.Quiet=false;microphoneGeneration=generation;microphoneRecordingEpoch=await microphone.BeginAsync();
            if(!pushToTalkHeld||!policy.Running||closed||generation!=ownerGeneration||!microphone.IsCurrentRecording(microphoneRecordingEpoch)){microphone.Cancel();return;}
            MicButton.Content="Listening… release to finish";UpdateMicrophoneIndicator();SetVisual("listening");
            Status("Microphone listening. Release to send; recognition stays local.");
        }
        catch(OperationCanceledException){}
        catch(Exception ex){pushToTalkHeld=false;Status("Microphone unavailable: "+ex.Message);}
    }
    partial void EndMicrophone()
    {
        if(!pushToTalkHeld)return;pushToTalkHeld=false;
        if(!policy.Running)microphone?.Cancel();else microphone?.End();
    }
    partial void DisposeMicrophone()=>microphone?.Dispose();
}
