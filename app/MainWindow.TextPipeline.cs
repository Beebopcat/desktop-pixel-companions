using System.Windows.Threading;
using AzerothCompanion.Services;

namespace AzerothCompanion;

public partial class MainWindow
{
    private readonly DispatcherTimer directTextPump = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SemaphoreSlim directObservationGate = new(1, 1);
    private bool directPumpBusy, automaticTextNeedsResume, lastReadWasDirectWaiting;

    private void InitializeDirectTextPump()
    {
        directTextPump.Tick += PumpDirectText;
        directTextPump.Start();
        Closed += (_, _) => directTextPump.Stop();
    }

    private static string SourceStatus(GameTextObservation observation) => observation.Source switch
    {
        GameTextSource.Accessibility => "Accessibility fallback · visible provider text; speaker unverified",
        GameTextSource.VerifiedHook => "Direct text · verified game hook",
        GameTextSource.DialogueFile => "Direct text · selected dialogue export",
        GameTextSource.ClipboardBridge => "Direct text · selected external exporter",
        _ => "No direct text source"
    };

    private async Task ConfigureAutomaticTextAsync(WindowChoice target, GameLaunchIdentity? identity, CancellationToken ct)
    {
        automaticTextNeedsResume = false;
        var readiness = await gameText.ConfigureAutomaticSourceAsync(target, identity, ct);
        ct.ThrowIfCancellationRequested();
        if (closed || selectedWindow != target || !policy.Running) return;
        TextSourceStatusText.Text = readiness.Message;
    }

    private void SuspendAutomaticText()
    {
        automaticTextNeedsResume = true;
        gameText.SuspendDirectSource();
        if (TextSourceStatusText != null) TextSourceStatusText.Text = "Direct dialogue paused by Quiet";
    }

    private async Task ResumeAutomaticTextAsync(CancellationToken ct)
    {
        if (!automaticTextNeedsResume || policy.Quiet || selectedWindow == null) return;
        ct.ThrowIfCancellationRequested();
        await ConfigureAutomaticTextAsync(selectedWindow, verifiedResearchGame, sessionCts.Token);
        ct.ThrowIfCancellationRequested();
    }

    private async void PumpDirectText(object? sender, EventArgs e)
    {
        if (closed || !policy.Running || policy.Quiet || directPumpBusy || observationCount > 0 ||
            selectedWindow == null || !gameText.HasActiveDirectSource) return;
        var target = selectedWindow; var epoch = sessionStartEpoch;
        var ct = sessionCts.Token; directPumpBusy = true; bool acquired = false;
        try
        {
            await directObservationGate.WaitAsync(ct); acquired = true;
            if (closed || !policy.Running || policy.Quiet || epoch != sessionStartEpoch || selectedWindow != target) return;
            EnsurePrimaryCaptureTarget();
            var observed = await gameText.ReadDirectAsync(target, ct);
            ct.ThrowIfCancellationRequested();
            if (closed || !policy.Running || policy.Quiet || epoch != sessionStartEpoch || selectedWindow != target) return;
            EnsurePrimaryCaptureTarget();
            if (observed.Status == GameTextStatus.TargetChanged)
            {
                StopSession(observed.Message); return;
            }
            if (!observed.HasText)
            {
                TextSourceStatusText.Text = observed.Message;
                if (observed.Status == GameTextStatus.ConnectedWaiting && AutoReadCheck.IsChecked == true &&
                    SpeechCheck.IsChecked == true && HasPendingDialogue && !responding &&
                    manualOperations == 0 && speechOperations == 0 && !audio.IsPlaying)
                    _ = DrainPublishedDialogueAsync(turnCts.Token, epoch);
                return;
            }
            // New dialogue can interrupt unsolicited remarks, never a user's explicit turn.
            if (isUnsolicitedTurn && observed.Status != GameTextStatus.Unchanged) NewTurn();
            latestGameObservation = observed; latestObservationAt = observed.Timestamp;
            latestText = observed.Text; ObservedText.Text = observed.Text;
            TextSourceStatusText.Text = SourceStatus(observed);
            ObserveQuestTitle(observed.Text);
            AcceptDirectDialogue(observed, AutoReadCheck.IsChecked == true && SpeechCheck.IsChecked == true);
            if (AutoReadCheck.IsChecked == true && SpeechCheck.IsChecked == true &&
                HasPendingDialogue && !responding && manualOperations == 0 && speechOperations == 0 && !audio.IsPlaying)
                _ = DrainPublishedDialogueAsync(turnCts.Token, epoch);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closed && epoch == sessionStartEpoch)
            {
                CancelDialogueQueue("Direct text source failed");
                TextSourceStatusText.Text = "Direct text unavailable: " + error.Message;
            }
        }
        finally { if (acquired) directObservationGate.Release(); directPumpBusy = false; }
    }

    private async Task DrainPublishedDialogueAsync(CancellationToken ct, long epoch)
    {
        try { await DrainDialogueQueueAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closed && epoch == sessionStartEpoch)
            {
                CancelDialogueQueue("Speech delivery failed");
                DialogueQueueStatusText.Text = "Narration paused: " + error.Message;
            }
        }
    }
}
