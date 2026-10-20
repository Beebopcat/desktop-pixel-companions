using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;

/// <summary>"Read my screen": pick a whole display, OCR it, and have the selected companion read it aloud. Independent of the game-session pipeline.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer screenTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<string> screenSeen = new(StringComparer.Ordinal);
    private CancellationTokenSource screenCts = new();
    private bool screenBusy;

    private void InitializeScreenReader()
    {
        screenTimer.Tick += async (_, _) => { if (ScreenAutoCheck.IsChecked == true) await ScreenReadAsync(readAll: false); };
        // Remember the last window you used that is not this app, so Read / the hotkey mean "that one".
        var follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        follow.Tick += (_, _) => ScreenGrab.ActiveWindow.Poll((uint)Environment.ProcessId);
        follow.Start();
        RefreshDisplays();
    }

    private void RefreshDisplays()
    {
        try
        {
            var previous = (ScreenDisplayPicker.SelectedItem as ScreenDisplay)?.Device;
            var displays = new List<ScreenDisplay> { ScreenDisplay.ActiveWindowEntry };
            displays.AddRange(ScreenGrab.ListDisplays());
            ScreenDisplayPicker.ItemsSource = displays;
            // Default: whichever window you were just using; remembered across Refresh.
            ScreenDisplayPicker.SelectedItem = displays.FirstOrDefault(d => d.Device == previous) ?? displays[0];
        }
        catch (Exception ex) { ScreenStatusText.Text = ex.Message; }
    }

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e) => RefreshDisplays();
    private async void ScreenReadNow_Click(object sender, RoutedEventArgs e)
    {
        // A deliberate click clears an earlier Quiet and starts a fresh read of everything on that display.
        policy.Quiet = false; screenSeen.Clear();
        await ScreenReadAsync(readAll: true);
    }

    private void ScreenAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (ScreenAutoCheck.IsChecked == true)
        {
            policy.Quiet = false; screenSeen.Clear();
            screenTimer.Start();
            ScreenStatusText.Text = "Auto-read is on. New text on this display is read as it appears. Stop or Quiet turns it off.";
        }
        else { screenTimer.Stop(); ScreenStatusText.Text = "Auto-read is off."; }
    }

    private void ScreenStop_Click(object sender, RoutedEventArgs e) => StopScreenReading("Stopped. Nothing is being captured or spoken.");

    private void StopScreenReading(string message)
    {
        screenTimer.Stop(); screenCts.Cancel(); screenCts = new();
        if (ScreenAutoCheck.IsChecked == true) ScreenAutoCheck.IsChecked = false;
        CancelOwned();
        ScreenStatusText.Text = message; Status(message);
    }

    private static string NormalizeLine(string line) => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool LooksLikeProse(string line)
    {
        if (line.Length < 12) return line.Length >= 4 && line.EndsWith('.') ;
        int letters = line.Count(char.IsLetter);
        return letters >= line.Length * 0.6 && line.Contains(' ');
    }

    private async Task ScreenReadAsync(bool readAll)
    {
        if (screenBusy || closed) return;
        if (policy.Quiet) { if (ScreenAutoCheck.IsChecked == true) StopScreenReading("Quiet is on, so auto-read stopped."); return; }
        if (ScreenDisplayPicker.SelectedItem is not ScreenDisplay display) { ScreenStatusText.Text = "Choose a display first."; return; }
        screenBusy = true;
        var ct = screenCts.Token; var owner = generation;
        try
        {
            SetVisual("reading"); ScreenStatusText.Text = "Reading " + display.Label + "…";
            // Keep this app's own windows out of the picture so it never reads its own interface.
            CapturedFrame frame; string sourceName = display.Label;
            if (display.IsActiveWindow)
            {
                if (!ScreenGrab.ActiveWindow.TryDescribe(out var windowBounds, out var windowTitle))
                {
                    ScreenStatusText.Text = "No other window to read yet. Click the window or browser tab you want read, then press Read (or Ctrl+Shift+F10).";
                    SetVisual("idle"); return;
                }
                ScreenStatusText.Text = "Reading window: " + (string.IsNullOrWhiteSpace(windowTitle) ? "(untitled)" : windowTitle) + "…";
                sourceName = string.IsNullOrWhiteSpace(windowTitle) ? "the active window" : windowTitle;
                frame = ScreenGrab.CaptureRect(windowBounds, "window");
            }
            else frame = ScreenGrab.Capture(display);
            ScreenGrab.MaskOwnWindows(frame);
            try { frame = frame.Crop(Region()); } catch (ArgumentException) { /* invalid crop fields: read the whole display */ }
            var result = await ocr.RecognizeAsync(frame, ct);
            ct.ThrowIfCancellationRequested();
            var fresh = new List<string>();
            foreach (var raw in result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0))
            {
                var key = NormalizeLine(raw);
                if (screenSeen.Contains(key)) continue;
                if (!LooksLikeProse(raw)) continue;
                screenSeen.Add(key); fresh.Add(raw);
            }
            if (fresh.Count == 0)
            {
                ScreenStatusText.Text = readAll ? "No readable text found on " + display.Label + ". Try the other display or adjust the crop in Advanced." : "Watching " + display.Label + " for new text…";
                SetVisual("idle"); return;
            }
            var text = string.Join(' ', fresh);
            // Join wrapped lines back into sentences for natural narration.
            ObservedText.Text = (readAll ? "" : ObservedText.Text + "\n") + string.Join("\n", fresh);
            if (!readAll && text.Length < 24 && !text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?')) { foreach (var l in fresh) screenSeen.Remove(NormalizeLine(l)); SetVisual("idle"); return; }
            if (ScreenSpeakCheck.IsChecked != true) { ScreenStatusText.Text = $"Read {fresh.Count} lines from \"{sourceName}\" (see Observed text). Speaking is off."; SetVisual("idle"); return; }
            ScreenStatusText.Text = $"Reading {fresh.Count} lines from \"{sourceName}\" aloud with {VoiceLabel(VoiceFor(character.Id))}…";
            await SpeakScreenAsync(text, ct, owner);
            if (!ct.IsCancellationRequested && owner == generation) ScreenStatusText.Text = ScreenAutoCheck.IsChecked == true ? "Watching " + display.Label + " for new text…" : "Done.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ScreenStatusText.Text = "Could not read the screen: " + ex.Message; SetVisual("idle"); }
        finally { screenBusy = false; }
    }

    private async Task SpeakScreenAsync(string text, CancellationToken ct, long owner)
    {
        var phrases = SpeechPhrasing.Split(text).Where(p => p.Text.Length > 0).ToArray();
        if (phrases.Length == 0) return;
        var voiceId = VoiceFor(character.Id);
        bool Current() => !closed && !ct.IsCancellationRequested && owner == generation && !policy.Quiet;
        speechOperations++; PauseMicrophone();
        try
        {
            ScreenStatusText.Text = "Preparing the voice... the first line can take up to 30 seconds while the voice model loads, then it speaks without pauses.";
            Task<NarrationAudio?> pending = narration.RenderAsync(phrases[0].Text, voiceId, ct);
            for (int i = 0; i < phrases.Length; i++)
            {
                if (!Current()) return;
                var rendered = await pending;
                if (!Current()) return;
                if (rendered == null) { ScreenStatusText.Text = "The voice service did not answer. Open Kokoro Player and try again."; return; }
                // Start rendering the next phrase now, so it is ready when this one finishes (no gap between sentences).
                pending = i + 1 < phrases.Length ? narration.RenderAsync(phrases[i + 1].Text, voiceId, ct) : Task.FromResult<NarrationAudio?>(null);
                SetVisual("speaking");
                if (!await audio.PlayAsync(rendered.PcmFloat32, rendered.SampleRate, ct)) return;
                if (i + 1 < phrases.Length && !await SpeechPhrasing.WaitBetweenAsync(phrases[i], ct, Current)) return;
            }
        }
        finally { speechOperations--; if (Current()) SetVisual("idle"); await ResumeOpenMicrophoneAsync(); }
    }
}
