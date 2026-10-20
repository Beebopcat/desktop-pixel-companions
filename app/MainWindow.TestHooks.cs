using AzerothCompanion.Services;
namespace AzerothCompanion;
public partial class MainWindow
{
    internal Task FixtureStartAsync(WindowChoice window)=>StartSelectedWindowAsync(window);
    internal Task FixtureAskAsync(string text)=>AskAsync(text);
    internal void FixtureSetHybridConsent(bool value){ProcessingPicker.SelectedIndex=1;CloudConsentCheck.IsChecked=value;}
    internal void FixtureRevokeCloudConsent()=>CloudConsentCheck.IsChecked=false;
    internal bool FixtureIsResponding=>responding;
    internal string FixtureReply=>ReplyText.Text;
    internal bool FixtureRunning=>policy.Running;
    internal void FixtureQuiet()=>Quiet_Click(this,new System.Windows.RoutedEventArgs());
    internal void FixtureMicrophone(InputSpeechService input)=>microphoneFactory=()=>input;
    internal Task FixtureReadAsync()=>ReadOnceAsync();
    internal Task FixtureRefreshVoicesAsync()=>RefreshVoiceChoicesAsync();
    internal string FixtureVoiceFor(string id)=>VoiceFor(id);
    internal bool FixtureMicrophoneListening=>microphone?.IsListening==true;
    internal bool FixtureWatchBusy=>watchBusy;
    internal void FixtureWatchTick()=>Watch_Tick(this,EventArgs.Empty);
    internal void FixtureSetSpeaker(string? id)=>activeSpeaker=id;
    internal Task FixtureSpeakAsync(string text)=>SpeakAsync(text,turnCts.Token);
    internal Task FixturePresentAsync(string text)=>PresentConversationAsync(text,turnCts.Token);
    internal bool FixtureQuietMode=>policy.Quiet;
}
