using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AzerothCompanion.Services;

namespace AzerothCompanion;

public partial class MainWindow
{
    private readonly Func<WindowChoice, GameProcessWindow> resolveProfileWindow;
    private CancellationTokenSource? profileActionCancellation;
    private bool profileActionBusy;
    internal Func<nint, bool>? FixturePrimaryWindow { get; set; }
    private bool IsPrimaryGameWindow(nint handle) => FixturePrimaryWindow?.Invoke(handle) ?? MonitorDesktopService.IsWindowOnPrimary(handle);
    private string? displayedProfileDiagnostic;
    private sealed record RememberedGameRow(UserConfirmedGameProfile Profile, string State)
    { public string Label => Profile.Title + " · " + State; }

    private static string IdentityLabel(GameLaunchIdentity identity) => identity.Provenance switch
    {
        GameLaunchProvenance.UserConfirmedExecutable => "user-confirmed executable",
        GameLaunchProvenance.SteamManifest => "Steam installation metadata",
        _ => "selected game window"
    };
    private static GameProcessWindow ResolveRunningProfileWindow(WindowChoice choice)
    {
        if (!CaptureNative.IsWindow(choice.Handle)) throw new InvalidOperationException("The selected game window has closed.");
        CaptureNative.GetWindowThreadProcessId(choice.Handle, out uint pid);
        if (pid != choice.ProcessId) throw new InvalidOperationException("The selected window changed. Refresh and select the game again.");
        using var process = Process.GetProcessById(checked((int)pid));
        var path = process.MainModule?.FileName ?? throw new InvalidOperationException("The selected executable path cannot be read.");
        return new(pid, process.ProcessName, choice.Handle, LocalGameProfileStore.NormalizeExecutablePath(path));
    }
    private void ProfileWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        profileActionCancellation?.Cancel();
        if (!IsInitialized || ProfileExecutableText == null) return;
        try
        {
            ProfileExecutableText.Text = WindowPicker.SelectedItem is WindowChoice choice ? resolveProfileWindow(choice).ProcessPath : "Select an actual game window first.";
        }
        catch (Exception error) { ProfileExecutableText.Text = error.Message; }
    }
    private void RememberedGame_Changed(object sender, SelectionChangedEventArgs e)
    {
        profileActionCancellation?.Cancel();
        if (RememberedGamePicker.SelectedItem is RememberedGameRow row) ProfileTitleBox.Text = row.Profile.Title;
    }
    private async void RefreshRememberedGames_Click(object sender, RoutedEventArgs e) => await RefreshRememberedGamesAsync();
    private async Task RefreshRememberedGamesAsync()
    {
        try
        {
            string? oldId = (RememberedGamePicker.SelectedItem as RememberedGameRow)?.Profile.Id;
            var entries = await gameProfiles.ListAsync(automationLifetime.Token);
            var rows = await Task.Run(() => entries.Select(p =>
            {
                var check = gameProfiles.CheckMetadata(p);
                return new RememberedGameRow(p, check.State == GameProfileState.Ready ? "metadata unchanged; hash checked on detection" : check.State + ": " + check.Message);
            }).ToArray(), automationLifetime.Token);
            if (closed) return;
            RememberedGamePicker.ItemsSource = rows;
            RememberedGamePicker.SelectedItem = rows.FirstOrDefault(p => p.Profile.Id == oldId);
            ProfileStatusText.Text = rows.Length == 0 ? "No remembered games. Selecting a window creates nothing." : rows.Length + " local profile(s). Use the matching running window to reconfirm.";
            DisplayProfileDiagnostic();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) ProfileStatusText.Text = "Remembered games unavailable: " + error.Message; }
    }
    private async void RememberGame_Click(object sender, RoutedEventArgs e) => await ConfirmSelectedGameAsync(false);
    private async void ReconfirmGame_Click(object sender, RoutedEventArgs e) => await ConfirmSelectedGameAsync(true);
    private async Task ConfirmSelectedGameAsync(bool reconfirm)
    {
        if (profileActionBusy) return;
        if (WindowPicker.SelectedItem is not WindowChoice choice) { ProfileStatusText.Text = "Select the actual running game window first."; return; }
        var previous = (RememberedGamePicker.SelectedItem as RememberedGameRow)?.Profile;
        if (reconfirm && previous == null) { ProfileStatusText.Text = "Select the remembered profile to reconfirm."; return; }
        try
        {
            var expected = resolveProfileWindow(choice);
            if (reconfirm && !string.Equals(previous!.ExecutablePath, expected.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reconfirm requires the same exact executable. For a moved installation, forget its old profile and remember the new selected game.");
            var title = Core.GameKnowledgeText.ValidateTitle(ProfileTitleBox.Text);
            BeginProfileAction(verifiedResearchGame is { } active && string.Equals(active.ProcessPath,expected.ProcessPath,StringComparison.OrdinalIgnoreCase) ? active.SourceId : null);
            var ct = profileActionCancellation!.Token;
            var current = await gameProfiles.ListAsync(ct);
            var existing = current.FirstOrDefault(p => string.Equals(p.ExecutablePath, expected.ProcessPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) RevokeRememberedGame(existing.Id, "Remembered game is being reconfirmed. Session stopped; use Start explicitly to resume.");
            ProfileStatusText.Text = "Checking selected executable locally… Cancel or Quiet discards pending confirmation.";
            var saved = await Task.Run(() => gameProfiles.ConfirmAsync(title, expected.ProcessPath!, ct, token =>
            {
                token.ThrowIfCancellationRequested();
                if (resolveProfileWindow(choice) != expected) throw new InvalidOperationException("The selected running game changed during confirmation. Nothing was saved.");
                return Task.CompletedTask;
            }), ct);
            if (!closed) ProfileStatusText.Text = "Remembered " + saved.Title + " as a user-confirmed executable. Capture and research were not started.";
        }
        catch (OperationCanceledException) { if (!closed) ProfileStatusText.Text = "Confirmation cancelled. No pending result will start capture or research."; }
        catch (Exception error) { if (!closed) ProfileStatusText.Text = "Could not remember game: " + error.Message; }
        finally { await FinishProfileActionAsync(); }
    }
    private async void ForgetGame_Click(object sender, RoutedEventArgs e)
    {
        if (profileActionBusy) return;
        if (RememberedGamePicker.SelectedItem is not RememberedGameRow row) { ProfileStatusText.Text = "Choose the exact remembered game to forget."; return; }
        await ForgetGameAsync(row.Profile.Id);
    }
    private async Task ForgetGameAsync(string id)
    {
        if (profileActionBusy) return;
        BeginProfileAction(id);
        try
        {
            bool removed = await gameProfiles.RemoveAsync(id, profileActionCancellation!.Token);
            if (!closed) ProfileStatusText.Text = removed ? "Forgot the selected profile. Its game files are untouched; automatic restart remains held." : "That profile was already absent.";
        }
        catch (OperationCanceledException) { if (!closed) ProfileStatusText.Text = "Forget cancelled; automatic restart remains held."; }
        catch (Exception error) { if (!closed) ProfileStatusText.Text = "Could not forget profile: " + error.Message; }
        finally { await FinishProfileActionAsync(); }
    }
    private void BeginProfileAction(string? id)
    {
        profileActionBusy = true; CancelDetectionWork(); gameDetector.InvalidateConfirmedProfiles();
        profileActionCancellation?.Dispose(); profileActionCancellation = CancellationTokenSource.CreateLinkedTokenSource(automationLifetime.Token);
        if (id != null) RevokeRememberedGame(id, "Remembered game is being changed. Session stopped; use Start explicitly to resume.");
        ProfileActions.IsEnabled = false; RememberedGamePicker.IsEnabled = false;
    }
    private async Task FinishProfileActionAsync()
    {
        if (!profileActionBusy) return;
        gameDetector.InvalidateConfirmedProfiles(); CancelDetectionWork();
        try { if (!closed) { var status = ProfileStatusText.Text; await RefreshRememberedGamesAsync(); if (!closed) ProfileStatusText.Text = status; } }
        finally { profileActionBusy = false; if (!closed) { ProfileActions.IsEnabled = true; RememberedGamePicker.IsEnabled = true; } }
    }
    private void CancelGameProfile_Click(object sender, RoutedEventArgs e) => profileActionCancellation?.Cancel();
    private bool ReconcileRememberedSession(IReadOnlyList<GameLaunchIdentity> games)
    {
        var active = verifiedResearchGame ?? detectedLaunch;
        if (active?.Provenance != GameLaunchProvenance.UserConfirmedExecutable) return false;
        var fresh = games.FirstOrDefault(x => x.Provenance == active.Provenance && x.SourceId == active.SourceId && x.ProcessId == active.ProcessId && x.WindowHandle == active.WindowHandle && string.Equals(x.ProcessPath,active.ProcessPath,StringComparison.OrdinalIgnoreCase) && gameDetector.IsCurrent(x));
        if (fresh != null) { if (verifiedResearchGame != null) verifiedResearchGame = fresh; if (detectedLaunch?.SourceId == fresh.SourceId) detectedLaunch = fresh; return false; }
        RevokeRememberedGame(active.SourceId!, "Remembered game is unavailable, changed or forgotten. Capture and research stopped; explicitly reconfirm or select a window.");
        return true;
    }
    private void RevokeRememberedGame(string id, string message)
    {
        bool active = verifiedResearchGame?.SourceId == id && verifiedResearchGame.Provenance == GameLaunchProvenance.UserConfirmedExecutable;
        bool detected = detectedLaunch?.SourceId == id && detectedLaunch.Provenance == GameLaunchProvenance.UserConfirmedExecutable;
        if (!active && !detected) return;
        // Cancel the selected profile's authority, not unrelated Steam/manual sessions.
        if (active || selectedWindow == null)
        {
            manuallyStoppedGame = selectedWindow?.ProcessId ?? detectedLaunch?.ProcessId ?? 0;
            manualUnknownGameHold = manuallyStoppedGame == 0; CancelDetectionWork();
            StopSession(message);
        }
        if (detected) detectedLaunch = null;
        if (active) verifiedResearchGame = null;
    }
    private void DisplayProfileDiagnostic()
    {
        if (closed) return;
        if (gameDetector.ProfileDiagnostic is { } diagnostic) ProfileStatusText.Text = displayedProfileDiagnostic = "Remembered games: " + diagnostic;
        else { if (displayedProfileDiagnostic != null && ProfileStatusText.Text == displayedProfileDiagnostic) ProfileStatusText.Text = "Remembered game checks recovered. Sessions remain subject to Quiet and Stop."; displayedProfileDiagnostic = null; }
    }
}
