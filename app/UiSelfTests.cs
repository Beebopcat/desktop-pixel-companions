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
using AzerothCompanion.Services;

namespace AzerothCompanion;

/// <summary>Dispatcher-only integration checks. Only the generated fixture is captured;
/// model, research and narrator HTTP transports are replaced before MainWindow is created.</summary>
public static class UiSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        if (!PaidServicePolicy.IsAuthorized)
            return await LocalFirstUiSelfTests.RunAsync();
        if (Application.Current == null || !Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException("UI self-tests must run on the WPF application dispatcher.");
        var passed = new List<string>();
        var directory = Path.Combine(Environment.CurrentDirectory, "work", "ui-consent-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ledger = new BudgetLedger(Path.Combine(directory, "fake-budget.json"));
        using var modelHandler = new HeldModelHandler();
        using var modelHttp = new HttpClient(modelHandler) { Timeout = TimeSpan.FromSeconds(25) };
        using var narratorHandler = new ForbiddenNarratorHandler();
        using var narratorHttp = new HttpClient(narratorHandler);
        using var researchHandler = new HeldResearchHandler();
        using var researchHttp = new HttpClient(researchHandler) { Timeout = TimeSpan.FromSeconds(25) };
        using var research = new GameResearchService(researchHttp, ledger, () => "offline-fixture-key", cooldown: TimeSpan.Zero);
        var reasoning = new ReasoningClient(modelHttp, ledger, () => "offline-fixture-key");
        var narration = new NarrationClient(narratorHttp);
        var fixtureText = new TextBlock { Text = "FIRST CONSENT REQUEST\nQuest: Find the silver moon.",
            Foreground = Brushes.Black, FontSize = 32, Margin = new Thickness(20) };
        var fixture = new Window { Title = "Companion consent test — generated content only",
            Width = 780, Height = 245, Background = Brushes.White, Content = fixtureText,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        MainWindow? main = null;
        var activeTasks = new List<Task>();
        try
        {
            main = new MainWindow(reasoning, narration, research);
            main.GameContextBox.Text = "Generated offline integration test; no real game or personal context.";
            main.AutoReadCheck.IsChecked = false;
            main.BanterCheck.IsChecked = false;
            main.IncludeImageCheck.IsChecked = false;
            Check(!main.FixtureRunning && !main.FixtureIsResponding && modelHandler.Count == 0 && narratorHandler.Count == 0,
                "Constructing MainWindow must not start capture or inference.");
            passed.Add("UI construction leaves capture, inference and narration off.");
            Check(main.ResearchCheck.IsChecked != true && main.QuestResearchCheck.IsChecked != true && researchHandler.Count == 0 &&
                main.ResearchSources.Children.Count == 0 && string.IsNullOrEmpty(main.ResearchSummaryText.Text),
                "Research or quest sharing must be off, with no cached visible source context, at startup.");
            passed.Add("Research and visible-quest sharing default off; no research request is enabled by construction or saved preferences.");

            fixture.Show();
            await Task.Delay(400);
            var choice = new WindowChoice(new WindowInteropHelper(fixture).Handle,
                (uint)Environment.ProcessId, fixture.Title, "AzerothCompanionGeneratedFixture");
            await main.FixtureStartAsync(choice).WaitAsync(TimeSpan.FromSeconds(8));
            Check(main.FixtureRunning, "Generated WGC fixture session did not start.");
            main.FixtureSetHybridConsent(true);
            // Speech is deliberately enabled against a rejecting fake narrator. Even a
            // regression cannot obtain PCM or reach native playback in these tests.
            main.SpeechCheck.IsChecked = true;
            var baseline = main.FixtureReply;

            var first = modelHandler.Enqueue("STALE CONSENT ANSWER");
            var firstAsk = main.FixtureAskAsync("First consent fixture question."); activeTasks.Add(firstAsk);
            await first.Received.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(first.Body.Contains("silver", StringComparison.OrdinalIgnoreCase), "First request omitted generated WGC/OCR text.");
            Check(!first.Body.Contains("image_url", StringComparison.Ordinal), "Image sharing occurred while image permission was off.");

            fixtureText.Text = "SECOND CONSENT REQUEST\nQuest: Find the silver moon.";
            fixture.UpdateLayout();
            await Task.Delay(180);
            var queuedAsk = main.FixtureAskAsync("Second consent fixture question."); activeTasks.Add(queuedAsk);
            await WaitUntilAsync(() => main.ObservedText.Text.Contains("SECOND", StringComparison.OrdinalIgnoreCase),
                "Second request did not finish generated OCR.");
            Check(!queuedAsk.IsCompleted && modelHandler.Count == 1, "Second request was not held behind the in-flight model call.");
            main.FixtureRevokeCloudConsent(); // Actual CheckBox.Unchecked event, not direct token manipulation.
            await queuedAsk.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!main.FixtureIsResponding, "Revoking cloud consent left the UI responding.");
            first.Release.TrySetResult();
            await firstAsk.WaitAsync(TimeSpan.FromSeconds(5));
            await main.FixtureAskAsync("Consent is revoked; this must never send.");
            Check(modelHandler.Count == 1, "A pending or new cloud request was sent after consent revocation.");
            Check(main.FixtureReply == baseline, "A cancelled cloud reply or error replaced visible dialogue.");
            Check(narratorHandler.Count == 0, "A cancelled cloud reply reached narration.");
            var budget = await ledger.GetSnapshotAsync();
            Check(budget.UncertainRequests == 1 && budget.ReservedUsd > 0, "Cancelled in-flight request did not retain possible charges.");
            passed.Add("Actual cloud checkbox revocation cancels queued Hybrid work; no later fake HTTP request, stale dialogue or narration is sent.");
            passed.Add("UI cancellation retains the in-flight cloud reservation while the cancelled queued request creates no charge.");

            main.FixtureSetHybridConsent(true);
            var old = modelHandler.Enqueue("STALE OLD TURN ANSWER");
            var newer = modelHandler.Enqueue("STALE QUIET ANSWER");
            var oldAsk = main.FixtureAskAsync("Old turn fixture question."); activeTasks.Add(oldAsk);
            await old.Received.Task.WaitAsync(TimeSpan.FromSeconds(8));
            fixtureText.Text = "NEWEST TURN REQUEST\nQuest: Find the silver moon.";
            fixture.UpdateLayout();
            await Task.Delay(180);
            var newAsk = main.FixtureAskAsync("Newest turn fixture question."); activeTasks.Add(newAsk);
            await WaitUntilAsync(() => main.ObservedText.Text.Contains("NEWEST", StringComparison.OrdinalIgnoreCase),
                "Newest request did not finish generated OCR.");
            old.Release.TrySetResult();
            await newer.Received.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await oldAsk.WaitAsync(TimeSpan.FromSeconds(3));
            Check(main.FixtureIsResponding, "Old request completion cleared responding state owned by the newer request.");
            Check(main.FixtureReply == baseline, "An old request published stale dialogue.");
            passed.Add("An old turn finishing while a newer turn runs cannot publish its reply or clear the newer responding state.");

            main.FixtureQuiet(); // Actual Quiet handler, including speech and banter toggles.
            await newer.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            newer.Release.TrySetResult();
            await newAsk.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!main.FixtureIsResponding && main.SpeechCheck.IsChecked != true && main.BanterCheck.IsChecked != true,
                "Quiet left pending response, speech or banter enabled.");
            Check(main.FixtureReply == baseline, "Quiet allowed stale dialogue to publish.");
            Check(modelHandler.Count == 3 && narratorHandler.Count == 0, "Quiet allowed additional model or narration work.");
            passed.Add("Quiet cancels the current cloud turn and suppresses stale reply and speech; no narrator or playback is reached.");
            Check(main.ResearchCheck.IsChecked != true, "Starting or asking about a selected window enabled research implicitly.");

            main.ResearchTopicBox.Text = "Generated lore fixture";
            var beforeResearch = await ledger.GetSnapshotAsync();
            var failedResearch = researchHandler.Enqueue(HttpStatusCode.ServiceUnavailable);
            main.ResearchCheck.IsChecked = true; // Actual opt-in event triggers one fake Responses request.
            await failedResearch.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(researchHandler.Count == 1 && research.HasSessionConsent, "Research opt-in did not send exactly one fake request.");
            failedResearch.Release.TrySetResult();
            await WaitUntilAsync(() => !PrivateField<bool>(main, "researchBusy"), "Failed research did not finish.");
            Check(PrivateField<DateTimeOffset>(main, "nextResearchAttempt") == DateTimeOffset.MaxValue,
                "Failed research did not pause automatic retry scheduling.");
            InvokePrivate(main, "CheckBackgroundResearch");
            await Task.Delay(50);
            Check(researchHandler.Count == 1, "Background research retried after failure without explicit action.");
            var failedBudget = await ledger.GetSnapshotAsync();
            Check(failedBudget.UncertainRequests == beforeResearch.UncertainRequests + 1 &&
                failedBudget.ReservedUsd == beforeResearch.ReservedUsd + GameResearchService.MaximumRequestUsd,
                "Failed research did not preserve its full possible charge.");
            passed.Add("Actual research opt-in sends one fake request; failure retains its reservation and pauses automatic retries.");

            var staleResearch = researchHandler.Enqueue(HttpStatusCode.OK);
            // A real explicit input change may reset the pause, while the manual button
            // starts the request immediately without waiting for a wall-clock cooldown.
            main.ResearchTopicBox.Text = "Second generated lore fixture";
            Check(PrivateField<DateTimeOffset>(main, "nextResearchAttempt") != DateTimeOffset.MaxValue,
                "An explicit research-scope edit did not reset the paused schedule.");
            LogicalDescendants(main).OfType<Button>().Single(button => Equals(button.Content, "Research this topic"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await staleResearch.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Seed only visible synthetic UI data, never the service or a web request.
            main.ResearchSummaryText.Text = "GENERATED RESEARCH DISPLAY SENTINEL";
            main.ResearchSources.Children.Add(new TextBlock { Text = "Generated source placeholder" });
            var stop = LogicalDescendants(main).OfType<Button>().Single(button => Equals(button.Content, "Stop session"));
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await staleResearch.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            staleResearch.Release.TrySetResult();
            await WaitUntilAsync(() => !PrivateField<bool>(main, "researchBusy"), "Stopped research did not finish cancellation.");
            Check(!main.FixtureRunning && main.CloudConsentCheck.IsChecked != true && main.ResearchCheck.IsChecked != true &&
                main.ResearchSources.Children.Count == 0 && string.IsNullOrEmpty(main.ResearchSummaryText.Text) &&
                string.IsNullOrEmpty(main.ObservedText.Text), "Stop session did not clear capture and research display/consent.");
            await main.FixtureAskAsync("Stopped session must not infer.");
            Check(modelHandler.Count == 3 && narratorHandler.Count == 0, "Stopped session reached model or narrator transport.");
            Check(researchHandler.Count == 2 && !research.HasSessionConsent && research.GetCurrentContext().Length == 0,
                "Stopped research published old context, kept consent or sent another request.");
            passed.Add("Actual Stop session clears observed text, research display and sharing consent; later Ask sends nothing.");
            passed.Add("Stop revokes active research; its late fake completion cannot restore sources, context or consent.");
            passed.Add("Only the generated fixture window was captured; model, research and narration HTTP were fake, with no user-screen capture, microphone, paid API or audio playback.");
            return passed;
        }
        finally
        {
            main?.FixtureQuiet();
            modelHandler.ReleaseAll();
            research.SetSessionConsent(false);
            researchHandler.ReleaseAll();
            try { await Task.WhenAll(activeTasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (main != null) try { await WaitUntilAsync(() => !PrivateField<bool>(main, "researchBusy"), "Research cleanup timed out."); } catch { }
            main?.Close();
            fixture.Close();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message);
            await Task.Delay(25);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T PrivateField<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("Missing UI test field: " + name));

    private static void InvokePrivate(MainWindow window, string name) =>
        (typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing UI test method: " + name)).Invoke(window, null);

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject item)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }

    private sealed class HeldResponse(string text)
    {
        public string Text { get; } = text;
        public string Body { get; set; } = "";
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HeldModelHandler : HttpMessageHandler
    {
        private readonly List<HeldResponse> responses = [];
        private int count;
        public int Count => Volatile.Read(ref count);
        public HeldResponse Enqueue(string text)
        {
            var response = new HeldResponse(text);
            lock (responses) responses.Add(response);
            return response;
        }
        public void ReleaseAll() { lock (responses) foreach (var item in responses) item.Release.TrySetResult(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref count) - 1;
            HeldResponse held;
            lock (responses)
            {
                if (index >= responses.Count) throw new InvalidOperationException("Unexpected fake cloud send after cancellation.");
                held = responses[index];
            }
            Check(request.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/chat/completions", "Unexpected model endpoint.");
            held.Body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            using var registration = ct.Register(() => held.Cancelled.TrySetResult());
            held.Received.TrySetResult();
            // Intentionally model a server finishing after cancellation. The real client
            // must retain uncertain charges and the UI must discard every stale result.
            await held.Release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = ReasoningClient.CloudModel, usage = new { prompt_tokens = 100, completion_tokens = 30 },
                choices = new[] { new { message = new { content = held.Text } } }
            }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class HeldResearchHandler : HttpMessageHandler
    {
        private readonly List<HeldResponse> responses = [];
        private int count;
        public int Count => Volatile.Read(ref count);
        public HeldResponse Enqueue(HttpStatusCode status)
        {
            const string source = "https://example.com/generated-fixture";
            var claims = new { facts = new[] { new { text = "STALE GENERATED RESEARCH", source_urls = new[] { source } } },
                guidance = Array.Empty<object>(), lore = Array.Empty<object>(), theories = Array.Empty<object>() };
            var payload = JsonSerializer.Serialize(new
            {
                model = GameResearchService.Model, status = "completed", service_tier = "default",
                usage = new { input_tokens = 100, output_tokens = 50 },
                output = new object[]
                {
                    new { type = "web_search_call", status = "completed", action = new { type = "search",
                        sources = new[] { new { url = source, title = "Generated test provenance" } } } },
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text",
                        text = JsonSerializer.Serialize(claims), annotations = Array.Empty<object>() } } }
                }
            });
            var response = new HeldResponse(payload) { StatusCode = status };
            lock (responses) responses.Add(response);
            return response;
        }
        public void ReleaseAll() { lock (responses) foreach (var item in responses) item.Release.TrySetResult(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref count) - 1;
            HeldResponse held;
            lock (responses)
            {
                if (index >= responses.Count) throw new InvalidOperationException("Unexpected automatic research retry.");
                held = responses[index];
            }
            Check(request.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/responses", "Unexpected research endpoint.");
            held.Body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            using var registration = ct.Register(() => held.Cancelled.TrySetResult());
            held.Received.TrySetResult();
            await held.Release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            return new HttpResponseMessage(held.StatusCode)
                { Content = new StringContent(held.Text, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ForbiddenNarratorHandler : HttpMessageHandler
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref count);
            throw new InvalidOperationException("Cancelled UI work reached the fake narrator. No audio was rendered.");
        }
    }
}
