using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;

/// <summary>Actual WPF handlers with generated window/file fixtures and in-memory HTTP only.
/// Preserves the historical authorized cloud suite while verifying today's zero-cost policy.</summary>
internal static class LocalFirstUiSelfTests
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        if(Application.Current==null||!Application.Current.Dispatcher.CheckAccess())throw new InvalidOperationException("WPF dispatcher required.");
        var passed=new List<string>();
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);passed.Add(label);}
        var directory=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"work","ui-selftests","local-first-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var ledgerPath=Path.Combine(directory,"budget.json");
        const string sentinel="Historical ledger remains untouched by local-only UI tests.";
        await File.WriteAllTextAsync(ledgerPath,sentinel);
        var ledger=new BudgetLedger(ledgerPath);int keyReads=0;
        string Key(){keyReads++;return "offline-fixture-key";}
        using var modelHandler=new HeldLocalModelHandler();using var modelHttp=new HttpClient(modelHandler);
        using var narratorHandler=new ForbiddenHandler();using var narratorHttp=new HttpClient(narratorHandler);
        using var paidHandler=new ForbiddenHandler();using var paidHttp=new HttpClient(paidHandler);
        using var freeHandler=new HeldFreeHandler();using var freeHttp=new HttpClient(freeHandler);
        using var paidResearch=new GameResearchService(paidHttp,ledger,Key,cooldown:TimeSpan.Zero);
        using var freeResearch=new FreeGameResearchService(freeHttp,new GameKnowledgeStore(Path.Combine(directory,"knowledge")),cooldown:TimeSpan.Zero);
        var main=new MainWindow(new ReasoningClient(modelHttp,ledger,Key),new NarrationClient(narratorHttp),paidResearch,freeResearch);
        var generatedText=new TextBlock{Text="FIRST LOCAL REQUEST\nQuest: Find the silver moon.",FontSize=32,Foreground=Brushes.Black,Margin=new Thickness(20)};
        var fixture=new Window{Title="Generated integration fixture",Width=780,Height=245,Content=generatedText,
            Background=Brushes.White,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen};
        Window? secondFixture=null;
        var pending=new List<Task>();
        try
        {
            main.AutoReadCheck.IsChecked=false;main.BanterCheck.IsChecked=false;main.IncludeImageCheck.IsChecked=false;
            main.GameContextBox.Text="PRIVATE CONTEXT SENTINEL MUST NEVER BE SENT TO FREE RESEARCH";
            Check(!PaidServicePolicy.IsAuthorized&&PaidServicePolicy.AuthorizedSpendingUsd==0,"Paid authorization remains zero, independent of UI privacy consent.");
            Check(!main.FixtureRunning&&!main.FixtureMicrophoneListening&&modelHandler.Count==0&&paidHandler.Count==0&&narratorHandler.Count==0&&freeHandler.Count==0,
                "UI construction opens no capture, microphone, model, paid research or narrator request.");
            Check(main.ResearchCheck.IsChecked!=true&&main.ResearchSources.Children.Count==0&&main.ResearchSummaryText.Text.Length==0,
                "Manual startup has no enabled research or stale visible source context.");
            fixture.Show();await Task.Delay(250);
            var target=new WindowChoice(new WindowInteropHelper(fixture).Handle,(uint)Environment.ProcessId,fixture.Title,Process.GetCurrentProcess().ProcessName);
            Check(MonitorDesktopService.IsWindowOnPrimary(target.Handle),"Generated fixture is entirely on the actual Windows primary display.");
            await main.FixtureStartAsync(target);
            Check(main.FixtureRunning&&!main.FixtureQuietMode,"Starting a manual selected-window session stays idle and muted until explicitly requested.");
            main.FixtureSetHybridConsent(true);await main.FixtureAskAsync("Blocked cloud fixture question.");
            Check(modelHandler.Count==0&&paidHandler.Count==0&&keyReads==0&&!main.FixtureIsResponding,
                "Checking Hybrid consent cannot bypass zero-dollar authorization or read credentials.");
            main.ProcessingPicker.SelectedIndex=0;main.CloudConsentCheck.IsChecked=false;
            var baseline=main.FixtureReply;
            var old=modelHandler.Enqueue("STALE OLD LOCAL ANSWER");var newer=modelHandler.Enqueue("STALE QUIET LOCAL ANSWER");
            var oldAsk=main.FixtureAskAsync("Old local fixture question.");pending.Add(oldAsk);
            await old.Received.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(old.Body.Contains("silver",StringComparison.OrdinalIgnoreCase)&&!old.Body.Contains("\"images\"",StringComparison.Ordinal),
                "Local ask receives only generated OCR text while image sharing is disabled.");
            generatedText.Text="NEWEST LOCAL REQUEST\nQuest: Find the silver moon.";fixture.UpdateLayout();await Task.Delay(150);
            var newAsk=main.FixtureAskAsync("Newest local fixture question.");pending.Add(newAsk);
            await Until(()=>main.ObservedText.Text.Contains("NEWEST",StringComparison.Ordinal));
            Check(modelHandler.Count==1&&!newAsk.IsCompleted,"New local requests serialize behind an existing held local request.");
            old.Release.TrySetResult();await newer.Received.Task.WaitAsync(TimeSpan.FromSeconds(8));await oldAsk.WaitAsync(TimeSpan.FromSeconds(3));
            Check(main.FixtureIsResponding&&main.FixtureReply==baseline,"An old local completion cannot publish stale text or clear the new responding state.");
            main.FixtureQuiet();await newer.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));newer.Release.TrySetResult();await newAsk.WaitAsync(TimeSpan.FromSeconds(5));
            Check(main.FixtureQuietMode&&!main.FixtureIsResponding&&main.FixtureReply==baseline&&narratorHandler.Count==0,
                "Actual Quiet discards a late local response before dialogue or narration publication.");
            Check(Private<uint>(main,"manuallyStoppedGame")==target.ProcessId&&main.OpenMicCheck.IsChecked!=true&&main.AutoReadCheck.IsChecked!=true&&
                main.BanterCheck.IsChecked!=true&&main.QuestResearchCheck.IsChecked!=true&&main.ResearchCheck.IsChecked!=true,
                "Quiet latches automatic restart for the selected process and disables mic, watch, banter and free research.");

            main.GameTitleBox.Text="World of Warcraft";main.ResearchTopicBox.Text="generated gameplay systems";
            var failed=freeHandler.Enqueue(HttpStatusCode.ServiceUnavailable);
            main.ResearchCheck.IsChecked=true;await failed.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));failed.Release.TrySetResult();
            await Until(()=>!Private<bool>(main,"researchBusy"));
            Check(freeHandler.Count==1&&Private<DateTimeOffset>(main,"nextResearchAttempt")==DateTimeOffset.MaxValue,
                "Actual free research opt-in uses a free GET and pauses automatic retry when no identity is verified.");
            Invoke(main,"CheckBackgroundResearch");await Task.Delay(40);
            Check(freeHandler.Count==1&&paidHandler.Count==0&&!paidResearch.HasSessionConsent&&keyReads==0,
                "Failed public retrieval neither retries indefinitely nor enables the preserved paid provider.");
            var quietResearch=freeHandler.Enqueue(HttpStatusCode.OK);
            main.ResearchTopicBox.Text="quiet research fixture";ResearchButton(main).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await quietResearch.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));main.FixtureQuiet();
            await quietResearch.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));quietResearch.Release.TrySetResult();await Until(()=>!Private<bool>(main,"researchBusy"));
            Check(main.FixtureQuietMode&&main.ResearchCheck.IsChecked!=true&&main.ResearchSources.Children.Count==0&&main.ResearchSummaryText.Text.Length==0&&freeResearch.GetCurrentContext().Length==0,
                "Quiet cancels in-flight free retrieval and a late transport completion cannot restore source context.");

            var completed=freeHandler.Enqueue(HttpStatusCode.OK);main.ResearchTopicBox.Text="systems fixture";main.ResearchCheck.IsChecked=true;
            await completed.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));completed.Release.TrySetResult();await Until(()=>!Private<bool>(main,"researchBusy"));
            Check(main.ResearchSources.Children.Count==1&&freeResearch.GetCurrentContext().Contains("Coverage gaps:",StringComparison.Ordinal)&&
                freeResearch.GetCurrentContext().Contains("optimal builds",StringComparison.Ordinal),
                "Free article excerpts publish cited sources with explicit guide/build coverage limits.");
            Check(freeHandler.Urls.All(url=>!url.Contains("silver",StringComparison.OrdinalIgnoreCase)&&!url.Contains("PRIVATE",StringComparison.OrdinalIgnoreCase))&&
                freeHandler.Urls.All(url=>new Uri(url).Scheme=="https"),
                "Public retrieval sends only public title/topic over fixed HTTPS origins, omitting OCR and private context.");
            var stopping=freeHandler.Enqueue(HttpStatusCode.OK);main.ResearchTopicBox.Text="stop research fixture";ResearchButton(main).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await stopping.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));FindButton(main,"Stop session").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await stopping.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));stopping.Release.TrySetResult();await Until(()=>!Private<bool>(main,"researchBusy"));
            Check(!main.FixtureRunning&&main.ResearchSources.Children.Count==0&&main.ResearchSummaryText.Text.Length==0&&main.ObservedText.Text.Length==0&&
                main.CloudConsentCheck.IsChecked!=true&&freeResearch.GetCurrentContext().Length==0,
                "Stop session clears observed text, research display/context and cloud consent despite a late free response.");
            int priorModels=modelHandler.Count;await main.FixtureAskAsync("Stopped fixture cannot infer.");
            Check(modelHandler.Count==priorModels&&narratorHandler.Count==0,"A stopped session cannot reach local inference or narration.");

            // Two generated windows and explicitly synthetic launch identities exercise
            // the UI handoff. No process detector or real Steam game is consulted.
            Private<System.Windows.Threading.DispatcherTimer>(main,"watch").Stop();
            secondFixture=new Window{Title="Second generated research fixture",Width=500,Height=160,
                Content=new TextBlock{Text="GENERATED SECOND GAME ONLY",FontSize=24,Margin=new Thickness(20)},
                Background=Brushes.White,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen};
            secondFixture.Show();await Task.Delay(80);
            var secondTarget=new WindowChoice(new WindowInteropHelper(secondFixture).Handle,(uint)Environment.ProcessId,secondFixture.Title,target.ProcessName);
            var firstIdentity=new GameLaunchIdentity("First Dossier Game",target.ProcessId,target.ProcessName,target.Handle,null);
            var secondIdentity=new GameLaunchIdentity("Second Dossier Game",secondTarget.ProcessId,secondTarget.ProcessName,secondTarget.Handle,null);
            SetPrivate(main,"detectedLaunch",firstIdentity);main.ResearchTopicBox.Text="";await main.FixtureStartAsync(target);
            SetPrivate(main,"detectedLaunch",secondIdentity);
            int beforeSwitchRequests=freeHandler.Urls.Count;
            var firstGame=freeHandler.Enqueue(HttpStatusCode.OK);main.ResearchCheck.IsChecked=true;
            await firstGame.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(Private<GameLaunchIdentity?>(main,"verifiedResearchGame")?.Title==firstIdentity.Title&&
                Uri.UnescapeDataString(freeHandler.Urls[beforeSwitchRequests]).Contains(firstIdentity.Title,StringComparison.Ordinal),
                "Foreground detection changes cannot retarget research away from the captured game's verified identity snapshot.");
            await main.FixtureStartAsync(secondTarget);await firstGame.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            firstGame.Release.TrySetResult();await Until(()=>!Private<bool>(main,"researchBusy"));
            Check(main.GameTitleBox.Text==secondIdentity.Title&&main.ResearchCheck.IsChecked!=true&&
                main.ResearchSources.Children.Count==0&&freeResearch.GetCurrentContext()=="",
                "Switching actual selected generated windows cancels old research and clears context before new opt-in.");
            var secondGame=freeHandler.Enqueue(HttpStatusCode.OK);main.ResearchCheck.IsChecked=true;
            await secondGame.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));secondGame.Release.TrySetResult();
            await Until(()=>!Private<bool>(main,"researchBusy"));
            Check(freeResearch.CurrentDossier?.Title==secondIdentity.Title&&main.ResearchSources.Children.Count==1&&
                freeResearch.GetCurrentContext().Contains(secondIdentity.Title,StringComparison.Ordinal)&&
                !freeResearch.GetCurrentContext().Contains(firstIdentity.Title,StringComparison.Ordinal),
                "New-game checkbox research publishes only its own identity, citations and dossier after old completion.");
            SetPrivate(main,"detectedLaunch",firstIdentity);SetPrivate(main,"nextResearchAttempt",DateTimeOffset.MinValue);
            Invoke(main,"CheckBackgroundResearch");await Until(()=>!Private<bool>(main,"researchBusy"));
            Check(freeResearch.CurrentDossier?.Entries.Count==2&&freeResearch.CurrentDossier.Title==secondIdentity.Title&&
                main.ResearchSummaryText.Text.Contains("Lore and world",StringComparison.Ordinal),
                "Background research advances the selected game's next dossier topic while retaining its earlier sources.");
            main.ResearchCheck.IsChecked=false;
            Check(freeResearch.GetCurrentContext()==""&&main.ResearchSources.Children.Count==0,
                "Actual research checkbox revocation clears accumulated multi-topic context.");
            Check(freeHandler.Urls.All(url=>!url.Contains("silver",StringComparison.OrdinalIgnoreCase)&&!url.Contains("PRIVATE",StringComparison.OrdinalIgnoreCase))&&paidHandler.Count==0&&narratorHandler.Count==0,
                "Game-switch and dossier queue handlers send no generated OCR/private context and never use paid or narrator transports.");
            FindButton(main,"Stop session").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));secondFixture.Close();secondFixture=null;

            await main.FixtureStartAsync(target);main.ResearchCheck.IsChecked=false;
            var export=Path.Combine(directory,"literal.jsonl");const string literal="  Literal  words — exactly!";
            await File.WriteAllTextAsync(export,JsonSerializer.Serialize(new{text=literal,speaker="Jaina Proudmoore",sequence=1})+"\n",new UTF8Encoding(false));
            Private<GameTextService>(main,"gameText").Configure(new(FileProfile:new(export,target.ProcessName,GameTextFileFormat.JsonLines,true)));
            await main.FixtureReadAsync();
            Check(main.ObservedText.Text==literal&&main.FixtureReply==literal&&Private<GameTextObservation?>(main,"latestGameObservation")?.Source==GameTextSource.DialogueFile,
                "Actual Read preserves literal explicit export spelling/spacing before OCR fallback.");
            FindButton(main,"Stop session").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await main.FixtureStartAsync(target);await main.FixtureReadAsync();
            Check(!main.ObservedText.Text.Contains("Literal  words",StringComparison.Ordinal)&&Private<GameTextObservation?>(main,"latestGameObservation")==null,
                "A new session does not inherit file/clipboard permission, prior speaker routes or stale exported text.");
            main.SpeechCheck.IsChecked=false;fixture.Left=-100000;fixture.UpdateLayout();await Task.Delay(50);await main.FixtureReadAsync();
            Check(!main.FixtureRunning&&main.ObservedText.Text.Length==0,"Moving the selected fixture outside the primary display stops the session before another observation.");
            Check(await File.ReadAllTextAsync(ledgerPath)==sentinel&&keyReads==0&&paidHandler.Count==0,
                "Local/free UI actions leave historical accounting untouched and create no paid reservation.");
            Check(narratorHandler.Count==0&&!main.FixtureMicrophoneListening,"Integration checks use no audio renderer, native playback or real microphone.");
            return passed;
        }
        finally
        {
            main.FixtureQuiet();modelHandler.ReleaseAll();freeHandler.ReleaseAll();
            foreach(var task in pending)try{await task.WaitAsync(TimeSpan.FromSeconds(3));}catch{}
            main.Close();fixture.Close();secondFixture?.Close();
        }
    }
    private static async Task Until(Func<bool> condition)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));while(!condition())await Task.Delay(25,timeout.Token);}
    private static T Private<T>(MainWindow main,string name)=>(T)(typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(main)!);
    private static void SetPrivate<T>(MainWindow main,string name,T value)=>typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(main,value);
    private static void Invoke(MainWindow main,string name)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,null);
    private static Button ResearchButton(MainWindow main)=>FindButton(main,"Research this topic");
    private static Button FindButton(MainWindow main,string content)=>Descendants(main).OfType<Button>().Single(button=>Equals(button.Content,content));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private sealed class Held(string text,HttpStatusCode status=HttpStatusCode.OK)
    {
        internal string Text=text,Body="";internal HttpStatusCode Status=status;
        internal TaskCompletionSource Received=new(TaskCreationOptions.RunContinuationsAsynchronously),Cancelled=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class HeldLocalModelHandler:HttpMessageHandler
    {
        private readonly List<Held> plans=[];private int count;internal int Count=>Volatile.Read(ref count);
        internal Held Enqueue(string answer){var held=new Held(answer);lock(plans)plans.Add(held);return held;}
        internal void ReleaseAll(){lock(plans)foreach(var held in plans)held.Release.TrySetResult();}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.AbsoluteUri!="http://127.0.0.1:11434/api/chat")throw new InvalidOperationException("Unexpected paid/model route.");
            int index=Interlocked.Increment(ref count)-1;Held held;lock(plans)held=plans[index];held.Body=await request.Content!.ReadAsStringAsync(CancellationToken.None);
            using var registration=token.Register(()=>held.Cancelled.TrySetResult());held.Received.TrySetResult();await held.Release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            return Json(new{message=new{content=held.Text}});
        }
    }
    private sealed class HeldFreeHandler:HttpMessageHandler
    {
        private readonly Queue<Held> plans=[];private readonly List<Held> all=[];private int count;internal int Count=>Volatile.Read(ref count);
        internal readonly List<string> Urls=[];
        internal Held Enqueue(HttpStatusCode status){var held=new Held("",status);lock(plans){plans.Enqueue(held);all.Add(held);}return held;}
        internal void ReleaseAll(){lock(plans)foreach(var held in all)held.Release.TrySetResult();}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method!=HttpMethod.Get||request.RequestUri!.Scheme!="https"||request.RequestUri.Host is not ("en.wikipedia.org" or "www.bing.com"))throw new InvalidOperationException("Unexpected free request origin.");
            Interlocked.Increment(ref count);lock(Urls)Urls.Add(request.RequestUri.AbsoluteUri);
            if(request.RequestUri.Host=="www.bing.com")return new(HttpStatusCode.OK){Content=new StringContent("<rss><channel /></rss>")};
            string query=Uri.UnescapeDataString(request.RequestUri.Query);
            string game=query.Contains("First Dossier Game",StringComparison.Ordinal)||query.Contains("pageids=101",StringComparison.Ordinal)?"First Dossier Game":
                query.Contains("Second Dossier Game",StringComparison.Ordinal)||query.Contains("pageids=102",StringComparison.Ordinal)?"Second Dossier Game":"World of Warcraft";
            int page=game=="First Dossier Game"?101:game=="Second Dossier Game"?102:42;
            if(request.RequestUri.Query.Contains("pageids=",StringComparison.Ordinal))return Json(new{query=new{pages=new[]{new{pageid=page,title=game,extract=game+" is a video game.\n== Gameplay ==\nGenerated systems excerpt.\n== Plot ==\nGenerated plot excerpt."}}}});
            Held? held=null;lock(plans)if(plans.Count>0)held=plans.Dequeue();
            if(held!=null)
            {using var registration=token.Register(()=>held.Cancelled.TrySetResult());held.Received.TrySetResult();await held.Release.Task.WaitAsync(TimeSpan.FromSeconds(20));if(held.Status!=HttpStatusCode.OK)return new(held.Status);}
            return Json(new{query=new{search=new[]{new{pageid=page,title=game,snippet=game+" video game"}}}});
        }
    }
    private sealed class ForbiddenHandler:HttpMessageHandler
    {internal int Count;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Interlocked.Increment(ref Count);throw new InvalidOperationException("Forbidden real/paid/narrator test route.");}}
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
}
