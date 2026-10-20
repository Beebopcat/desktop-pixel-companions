using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;

public partial class MainWindow
{
    // Preserved only for compatibility/accounting. UI research never calls this
    // paid provider, even when a credential is present or consent is checked.
    private readonly GameResearchService research = new();
    private readonly FreeGameResearchService freeResearch = new();
    private WindowChoice? selectedWindow;
    private bool researchBusy;
    private readonly ResearchWorkGate researchWorkGate = new();
    private long researchGeneration;
    private DateTimeOffset nextResearchAttempt;
    private string detectedQuest = "", detectedGameName = "", questionResearchTopic = "";
    private GameLaunchIdentity? verifiedResearchGame;

    private void IdentifyGame(WindowChoice window, GameLaunchIdentity? authority = null)
    {
        selectedWindow = window;
        var current = authority ?? detectedLaunch;
        verifiedResearchGame=current is {IsVerified:true} launch&&gameDetector.IsCurrent(launch)&&launch.WindowHandle==window.Handle&&launch.ProcessId==window.ProcessId?launch:null;
        var identity = GameResearchIdentity.Identify(window.Title, window.ProcessName);
        detectedGameName = verifiedResearchGame?.Title ?? identity.Name;
        GameTitleBox.Text = detectedGameName;
        GameIdentityText.Text = detectedGameName + " · " + (verifiedResearchGame is { } known ? IdentityLabel(known) : "selected window; correct its public title in Research");
        overlay?.ConfigureFootprint(window.Handle, FootprintSlider.Value / 100);
    }

    private bool CanResearch => !closed && ResearchCheck.IsChecked == true && selectedWindow != null;

    private void ResearchPermission_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || ResearchSources == null) return;
        researchGeneration++; research.SetSessionConsent(false); freeResearch.CancelPending();
        ResearchSummaryText.Text = ""; ResearchSources.Children.Clear(); nextResearchAttempt = DateTimeOffset.MinValue;
        if (!CanResearch) { ResearchStatusText.Text = ResearchCheck.IsChecked == true ? "Select or detect a game first." : "Free research off"; if (policy.Running) NewTurn(); return; }
        _ = RefreshResearchAsync();
    }

    private void ResearchScope_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || ResearchSources == null) return;
        questionResearchTopic = ""; researchGeneration++; freeResearch.CancelPending(clearContext:ReferenceEquals(sender,GameTitleBox)); nextResearchAttempt = DateTimeOffset.UtcNow.AddSeconds(3);
        ResearchSummaryText.Text = ""; ResearchSources.Children.Clear();
        if (policy.Running) NewTurn();
        ResearchStatusText.Text = ResearchCheck.IsChecked == true ? "Context changed · free sources will refresh after the cooldown" : "Free research off";
    }

    private async void ResearchNow_Click(object sender, RoutedEventArgs e)
    {
        if (ResearchCheck.IsChecked != true) ResearchCheck.IsChecked = true;
        else await RefreshResearchAsync();
    }
    private void CheckBackgroundResearch()
    {
        if (CanResearch && !researchBusy && DateTimeOffset.UtcNow >= nextResearchAttempt) _ = RefreshResearchAsync();
    }

    private void ObserveQuestTitle(string text)
    {
        if (QuestResearchCheck.IsChecked != true || ResearchCheck.IsChecked != true) return;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string title = "";
        for (int i = 0; i < lines.Length - 1; i++)
            if (lines[i].Equals("Quest", StringComparison.OrdinalIgnoreCase) || lines[i].Equals("Quest Log", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = lines[i + 1];
                if (candidate.Length is >= 4 and <= 100 && !candidate.Contains('@') && !candidate.Contains("http", StringComparison.OrdinalIgnoreCase)) title = candidate;
                break;
            }
        if (title.Length == 0 || title == detectedQuest) return;
        detectedQuest = title; questionResearchTopic = ""; researchGeneration++; freeResearch.CancelPending(clearContext:false);
        ResearchSummaryText.Text = ""; ResearchSources.Children.Clear(); nextResearchAttempt = DateTimeOffset.UtcNow.AddSeconds(3);
    }

    private async Task RefreshResearchAsync(CancellationToken requestCancellation = default, bool waitForCurrent = false)
    {
        if (!CanResearch) return;
        using var slot = await researchWorkGate.EnterAsync(waitForCurrent, requestCancellation);
        if (slot == null || !CanResearch) return;
        requestCancellation.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(automationLifetime.Token, requestCancellation);
        var mine = researchGeneration; researchBusy = true;
        try
        {
            // Only a public title and the explicitly enabled short topic leave the
            // computer. Screen text, images, private context and audio are omitted.
            var name = GameKnowledgeText.ValidateTitle(GameTitleBox.Text);
            var topic = ResearchTopicBox.Text.Trim();
            if (topic.Length == 0) topic = questionResearchTopic;
            if (topic.Length == 0 && QuestResearchCheck.IsChecked == true) topic = detectedQuest;
            topic = GameKnowledgeText.ValidateTopic(topic);
            var identity=verifiedResearchGame is {IsVerified:true} verified&&gameDetector.IsCurrent(verified)&&verified.WindowHandle==selectedWindow!.Handle&&verified.ProcessId==selectedWindow.ProcessId&&GameKnowledgeText.SameGame(name,verified.Title)?verified:null;
            var storeId=identity?.StoreId;
            ResearchStatusText.Text = "Retrieving free public sources for " + name + (topic.Length > 0 ? " · " + topic : "") + "…";
            var result = topic.Length==0&&identity is not null?
                await freeResearch.AdvanceDossierAsync(identity,lifetime.Token):await freeResearch.GetResearchAsync(name, topic, lifetime.Token, storeId);
            if (closed || mine != researchGeneration || !CanResearch) return;
            if (identity is not null&&!gameDetector.IsCurrent(identity)) { freeResearch.CancelPending(); return; }
            nextResearchAttempt = result.Status switch
            {
                FreeGameResearchStatus.Ready or FreeGameResearchStatus.Partial or FreeGameResearchStatus.Cached => identity is not null&&topic.Length==0?
                    result.RetryAtUtc??DateTimeOffset.UtcNow.AddSeconds(30):DateTimeOffset.UtcNow.AddMinutes(20),
                FreeGameResearchStatus.Throttled => result.RetryAtUtc ?? DateTimeOffset.UtcNow.AddSeconds(30),
                _ => DateTimeOffset.MaxValue
            };
            ResearchStatusText.Text = result.Message;
            ResearchSummaryText.Text = result.Message + "\n\n" + (result.Coverage is null?"":string.Join("\n",result.Coverage.Select(item=>$"{item.Label}: {item.Status}"))+"\n\n")+
                string.Join("\n", result.Sources.Select(source => $"[{source.Number}] {source.Title} — {source.Reliability} · {source.Kind}"))
                + (result.Sources.Count > 0 ? "\n\nSource excerpts are available to the companion as reference context. Links may contain story spoilers; open them when you want to read the source." : "");
            ResearchSources.Children.Clear();
            foreach (var source in result.Sources)
            {
                var button = new Button { Content = $"[{source.Number}] {source.Title}", HorizontalContentAlignment = HorizontalAlignment.Left,
                    ToolTip = $"{source.Reliability}\n{source.Kind}\n{source.Url}\nRetrieved {source.RetrievedUtc:u}" };
                button.Click += (_, _) =>
                {
                    try { if (GameKnowledgeText.IsPublicSourceUrl(source.Url)) Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true }); }
                    catch (Exception error) { ResearchStatusText.Text = "Could not open source: " + error.Message; }
                };
                ResearchSources.Children.Add(button);
            }
        }
        catch (OperationCanceledException) { if (mine == researchGeneration) nextResearchAttempt = DateTimeOffset.MaxValue; }
        catch (Exception error)
        {
            if (mine == researchGeneration) { ResearchStatusText.Text = "Free research paused: " + error.Message; nextResearchAttempt = DateTimeOffset.MaxValue; }
        }
        finally { researchBusy = false; }
    }

    private void QueueQuestionResearch(string question)
    {
        if (ResearchCheck.IsChecked != true || QuestionResearchCheck.IsChecked != true || !policy.Running || ResearchTopicBox.Text.Trim().Length > 0) return;
        var bounded = question.Trim(); if (bounded.Length > 300){ResearchStatusText.Text="Public question research was skipped: use a topic of at most 300 characters.";return;}
        if (questionResearchTopic == bounded) return;
        questionResearchTopic = bounded; researchGeneration++; freeResearch.CancelPending(clearContext:false);
        ResearchSummaryText.Text = ""; ResearchSources.Children.Clear(); nextResearchAttempt = DateTimeOffset.MinValue;
    }

    private void EndResearchSession()
    {
        researchGeneration++; research.SetSessionConsent(false); freeResearch.CancelPending();
        selectedWindow = null;verifiedResearchGame=null; detectedQuest = ""; questionResearchTopic = "";
        if (IsInitialized && ResearchCheck != null)
        {
            ResearchCheck.IsChecked = false; ResearchSummaryText.Text = ""; ResearchSources.Children.Clear();
            ResearchStatusText.Text = "Free research off · session ended";
        }
    }
}
