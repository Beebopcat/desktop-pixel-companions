using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AzerothCompanion.Services;

namespace AzerothCompanion;
public static class InteractionSelfTests
{
    public static async Task<object> RunAsync(string previewPath)
    {
        var passed=new List<string>();
        void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);passed.Add(message);}
        static async Task Until(Func<bool> predicate){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));while(!predicate())await Task.Delay(20,timeout.Token);}
        using var handler=new FakeNarrator();using var http=new HttpClient(handler);
        var engines=new List<FakeMicrophone>();
        using var input=new InputSpeechService(()=>{var e=new FakeMicrophone();engines.Add(e);return e;},()=>["en-US"]);
        var main=new MainWindow(testReasoning:null,testNarration:new NarrationClient(http));main.FixtureMicrophone(input);
        var fixture=new Window{Title="Companion generated reading fixture",Width=750,Height=230,ShowInTaskbar=false,ShowActivated=false,Background=Brushes.White,
            Content=new TextBlock{Text="A QUICK GAME READING.\nQuest: Follow the river.",FontSize=30,Foreground=Brushes.Black,Margin=new Thickness(25)}};
        var pending=new List<Task>();
        try
        {
            Check(!main.FixtureRunning&&!input.IsListening&&engines.Count==0,"Startup does not open microphone or capture.");
            await main.FixtureRefreshVoicesAsync();
            main.DuoCheck.IsChecked=true;main.PartnerPicker.SelectedItem=Core.Characters.Find("jaina");
            main.PrimaryVoicePicker.SelectedValue="af_heart";main.PartnerVoicePicker.SelectedValue="bm_george";
            Check(main.FixtureVoiceFor("sylvanas")=="af_heart"&&main.FixtureVoiceFor("jaina")=="bm_george","Each character retains a separate chosen voice.");
            fixture.Show();await Task.Delay(250);
            await main.FixtureStartAsync(new WindowChoice(new WindowInteropHelper(fixture).Handle,(uint)Environment.ProcessId,fixture.Title,System.Diagnostics.Process.GetCurrentProcess().ProcessName));
            main.SendVoiceCheck.IsChecked=false;main.OpenMicCheck.IsChecked=true;
            await Until(()=>input.IsListening);
            engines.Last().Emit("What is on this screen?");
            await Until(()=>main.QuestionBox.Text=="What is on this screen?");
            Check(input.IsListening&&engines.Count==1,"Open mic delivers an utterance without holding a button or reopening input.");
            main.AutoReadCheck.IsChecked=true;main.BanterCheck.IsChecked=true;main.QuestResearchCheck.IsChecked=true;main.SpeechCheck.IsChecked=true;
            Buttons(main).Single(b=>Equals(b.Content,"Quiet / cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(main.FixtureQuietMode&&!input.IsListening&&main.OpenMicCheck.IsChecked!=true&&main.SpeechCheck.IsChecked!=true&&main.AutoReadCheck.IsChecked!=true&&main.BanterCheck.IsChecked!=true&&main.QuestResearchCheck.IsChecked!=true,"The actual Quiet button stops mic, speech, reading, banter and automatic quest observation.");
            await input.WaitForStoppedAsync();
            main.QuestionBox.Text="quiet sentinel";engines[0].Emit("old queued transcript");await Task.Delay(40);
            Check(main.QuestionBox.Text=="quiet sentinel","A stopped microphone cannot publish a stale transcript.");
            main.SpeechCheck.IsChecked=true;
            var first=handler.Enqueue();var read=main.FixtureReadAsync();pending.Add(read);
            await first.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(first.Voice=="af_heart"&&main.ObservedText.Text.Contains("Follow the river",StringComparison.OrdinalIgnoreCase),"Screen reading uses the primary character's assigned voice and the generated captured text.");
            main.AutoReadCheck.IsChecked=true;main.FixtureWatchTick();main.FixtureWatchTick();await Task.Delay(30);
            Check(handler.RenderCount==1,"Automatic watch cannot enqueue extra renders while Read is waiting for speech.");
            main.FixtureQuiet();await read.WaitAsync(TimeSpan.FromSeconds(2));first.Release.TrySetResult();await Task.Delay(80);
            Check(main.SpeechCheck.IsChecked!=true&&main.FixtureQuietMode,"Quiet returns while an existing render finishes and rejects its late result.");
            main.SpeechCheck.IsChecked=true;main.FixtureSetSpeaker("jaina");
            var second=handler.Enqueue();var speak=main.FixtureSpeakAsync("A generated partner reply.");pending.Add(speak);
            await second.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(second.Voice=="bm_george","The second character's speech uses its own assigned voice.");
            main.FixtureQuiet();try{await speak.WaitAsync(TimeSpan.FromSeconds(2));}catch(OperationCanceledException){}second.Release.TrySetResult();await Task.Delay(80);
            var child=new TextBlock{Text="Quiet"};var button=new Button{Content=child};
            Check(OverlayWindow.IsInteractiveSource(child)&&!OverlayWindow.IsInteractiveSource(new Border()),"Overlay buttons are excluded from drag handling.");
            Check(MainWindow.IsQuietCommand("Stop talking!")&&!MainWindow.IsQuietCommand("Why is the village quiet?"),"Voice cancellation uses whole commands and does not match ordinary questions.");
            main.OpenMicCheck.IsChecked=true;await Until(()=>input.IsListening);
            engines.Last().Emit("quiet");await Until(()=>main.OpenMicCheck.IsChecked!=true);
            Check(main.FixtureQuietMode&&!input.IsListening,"The recognized Quiet command disables continuous listening.");
            await input.WaitForStoppedAsync();
            main.ReplyText.Text="Controls verified using generated text, simulated microphone input and held narrator requests. No audio was played.";
            main.QuestionBox.Text="";main.Show();await Task.Delay(200);main.SavePreview(previewPath);
            return new{allPassed=true,passed,realMicrophoneOpened=false,modelInference=false,paidApiCalled=false,audioPlayed=false,gameCaptured=false,generatedFixtureCaptured=true,previewPath};
        }
        finally
        {
            main.FixtureQuiet();handler.ReleaseAll();
            foreach(var task in pending)try{await task.WaitAsync(TimeSpan.FromSeconds(2));}catch{}
            main.Close();fixture.Close();await input.WaitForStoppedAsync();
        }
    }
    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {if(child is Button b)yield return b;foreach(var nested in Buttons(child))yield return nested;}
    }
    private sealed class FakeMicrophone:ISpeechInputEngine
    {
        public event EventHandler<SpeechEngineUtterance>? Recognized;
        public event EventHandler<SpeechEngineCompletion>? Completed;
        public void Emit(string text)=>Recognized?.Invoke(this,new(text));
        public void Start(){}public void Stop()=>Completed?.Invoke(this,new(false));
        public void Cancel(){}public void DisconnectInput(){}public void Dispose(){}
    }
    private sealed class Pending
    {
        public string Voice="";
        public TaskCompletionSource Received=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class FakeNarrator:HttpMessageHandler
    {
        private readonly Queue<Pending> pending=new();private readonly List<Pending> all=new();
        public int RenderCount;
        public Pending Enqueue(){var p=new Pending();pending.Enqueue(p);all.Add(p);return p;}
        public void ReleaseAll(){foreach(var p in all)p.Release.TrySetResult();}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri!.AbsolutePath=="/voices")return Json(new{ok=true,voices=new[]{new{id="af_heart",label="Heart",lang="en-us"},new{id="bm_george",label="George",lang="en-gb"},new{id=NarrationClient.ApprovedProfile,label="Lorath B",lang="en-gb"}}});
            if(request.RequestUri.AbsolutePath=="/status")return Json(new{ready=true,state="idle",voice="foreground-sentinel"});
            if(request.RequestUri.AbsolutePath!="/render-kokoro")throw new InvalidOperationException("Unexpected narrator route.");
            RenderCount++;var p=pending.Dequeue();using var payload=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));p.Voice=payload.RootElement.GetProperty("voice").GetString()!;p.Received.TrySetResult();
            await p.Release.Task.WaitAsync(TimeSpan.FromSeconds(20));return new(HttpStatusCode.Conflict);
        }
        private static HttpResponseMessage Json(object data)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data),Encoding.UTF8,"application/json")};
    }
}

