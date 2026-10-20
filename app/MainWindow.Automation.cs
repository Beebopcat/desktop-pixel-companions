using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AzerothCompanion.Services;

namespace AzerothCompanion;

public partial class MainWindow
{
    private readonly GameLaunchDetector gameDetector;
    private readonly LocalGameProfileStore gameProfiles;
    private CancellationTokenSource detectionCancellation = new();
    private readonly CancellationTokenSource automationLifetime = new();
    private readonly DispatcherTimer gameDetectionTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
    private bool injectedTestMode, automationInitialized, detectionBusy, manualUnknownGameHold;
    private long automationGeneration;
    private uint manuallyStoppedGame;
    private DateTimeOffset nextAutomaticStart;
    private GameLaunchIdentity? detectedLaunch;

    private void InitializeGameAutomation()
    {
        // Test and explicit live-command launches must remain isolated from the
        // user's other games and must never initiate automatic network work.
        if (Core.FunctionalTestMode.Active || automationInitialized || injectedTestMode || Environment.GetCommandLineArgs().Skip(1).Any(argument => argument.StartsWith("--", StringComparison.Ordinal))) return;
        automationInitialized = true;
        gameDetectionTimer.Tick += async (_, _) => await DetectGameAsync();
        gameDetectionTimer.Start();
        _ = DetectGameAsync();
        PopOut_Click(this, new RoutedEventArgs());
    }

    private void AutomaticGames_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || !automationInitialized || closed) return;
        if (AutoDetectCheck.IsChecked != true)
        {
            LatchManualGameStop();
            StopSession("Automatic game observation off. Capture and speech stopped.");
        }
        else
        {
            manuallyStoppedGame = 0; manualUnknownGameHold = false; nextAutomaticStart = DateTimeOffset.MinValue;
            _ = DetectGameAsync();
        }
    }

    private void LatchManualGameStop()
    {
        CancelDetectionWork(); profileActionCancellation?.Cancel();
        if (!policy.Running) { sessionStartEpoch++; sessionCts.Cancel(); }
        manuallyStoppedGame = selectedWindow?.ProcessId ?? detectedLaunch?.ProcessId ?? 0;
        manualUnknownGameHold = manuallyStoppedGame == 0;
    }

    private async Task DetectGameAsync()
    {
        if (closed || injectedTestMode || detectionBusy || profileActionBusy || policy.Quiet || manualUnknownGameHold || AutoDetectCheck.IsChecked != true || !automationInitialized) return;
        detectionBusy = true;
        var mine = automationGeneration;
        try
        {
            if (policy.Running && selectedWindow != null && !IsPrimaryGameWindow(selectedWindow.Handle))
                StopSession("Capture paused: the selected game is minimized or outside the primary display.");
            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(automationLifetime.Token, detectionCancellation.Token);
            var games = await gameDetector.PollAsync(pollCancellation.Token);
            if (closed || mine != automationGeneration || AutoDetectCheck.IsChecked != true) return;
            DisplayProfileDiagnostic();
            if (ReconcileRememberedSession(games)) return;
            if (manuallyStoppedGame != 0 && games.All(game => game.ProcessId != manuallyStoppedGame)) manuallyStoppedGame = 0;
            var primaryGames = games.Where(game => game.IsVerified && gameDetector.IsCurrent(game) && IsPrimaryGameWindow(game.WindowHandle)).ToArray();
            var foreground = GetForegroundWindowForDetection();
            var candidate = primaryGames.FirstOrDefault(game => game.WindowHandle == foreground) ??
                primaryGames.FirstOrDefault(game => game.WindowHandle == selectedWindow?.Handle) ??
                (primaryGames.Length == 1 ? primaryGames[0] : null);
            if (candidate == null)
            {
                if (!policy.Running) GameIdentityText.Text = primaryGames.Length > 1 ? "Several primary-display games detected. Focus or select the one to observe." : "Waiting for a verified game on the primary display…";
                return;
            }
            detectedLaunch = candidate;
            if (policy.Running || candidate.ProcessId == manuallyStoppedGame || DateTimeOffset.UtcNow < nextAutomaticStart) return;
            // Automatic capture is restricted to a path matched to a game manifest;
            // arbitrary titles/browser windows are never used as substitutes.
            var windows = capture.ListWindows();
            var choice = windows.FirstOrDefault(window => window.Handle == candidate.WindowHandle && window.ProcessId == candidate.ProcessId);
            if (choice == null || !IsPrimaryGameWindow(choice.Handle)) return;
            WindowPicker.ItemsSource = windows; WindowPicker.SelectedItem = choice;
            nextAutomaticStart = DateTimeOffset.UtcNow.AddSeconds(30);
            await StartSelectedWindowAsync(choice, candidate, mine);
            if (closed || mine != automationGeneration || !gameDetector.IsCurrent(candidate) || policy.Quiet || AutoDetectCheck.IsChecked != true || !policy.Running || selectedWindow?.Handle != choice.Handle) return;
            GameIdentityText.Text = candidate.Title + " · " + IdentityLabel(candidate) + " · automatically observing on primary display · muted";
            SessionBadge.Text = "●  AUTO OBSERVING · " + candidate.Title + " · MUTED";
            // User requested automatic game research. Only its public title is used;
            // quest titles/questions still have their own visible sharing controls.
            ResearchCheck.IsChecked = true;
            Status("Automatically observing " + candidate.Title + " on the primary display. Speech and microphone remain off; Quiet or Stop holds this game.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            nextAutomaticStart = DateTimeOffset.UtcNow.AddSeconds(30);
            if (!closed) GameIdentityText.Text = "Automatic detection paused: " + error.Message;
        }
        finally { detectionBusy = false; }
    }

    private void EnsurePrimaryCaptureTarget()
    {
        if (selectedWindow != null && IsPrimaryGameWindow(selectedWindow.Handle)) return;
        if (policy.Running) StopSession("Capture stopped: the selected window must stay entirely on the primary display.");
        throw new InvalidOperationException("Select a visible game window entirely on the primary display.");
    }

    private void HandleCaptureFailure(CaptureNotification notice)
    {
        if (closed || notice.Generation != capture.Generation || !policy.Running) return;
        LatchManualGameStop(); StopSession(notice.Message);
    }

    private void DisposeGameAutomation()
    {
        gameDetectionTimer.Stop(); automationLifetime.Cancel(); detectionCancellation.Cancel(); profileActionCancellation?.Cancel(); freeResearch.CancelPending(); freeResearch.Dispose();
    }

    private void CancelDetectionWork()
    {
        automationGeneration++; var old = detectionCancellation; detectionCancellation = new(); old.Cancel(); old.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetForegroundWindowForDetection();
}
