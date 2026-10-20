using System.Windows;
using System.Windows.Controls;
using AzerothCompanion.Services;

namespace AzerothCompanion;

/// <summary>"Play voice on" picker. The choice is shared with Kokoro Player and never changes Windows' own default device.</summary>
public partial class MainWindow
{
    private const string DefaultOutputLabel = "Windows default";
    private bool loadingOutputs;

    private void InitializeAudioOutput()
    {
        AudioOutputSelection.FallbackUsed += message => Dispatcher.BeginInvoke(() => { Status(message); if (ScreenStatusText != null) ScreenStatusText.Text = message; });
        RefreshAudioOutputs();
    }

    private void RefreshAudioOutputs()
    {
        loadingOutputs = true;
        try
        {
            var names = new List<string> { DefaultOutputLabel };
            names.AddRange(AudioOutputSelection.ListOutputs().Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            var saved = AudioOutputSelection.SavedName();
            // Keep a saved-but-disconnected device visible so the choice is not lost silently.
            if (saved != null && !names.Any(n => n.Equals(saved, StringComparison.OrdinalIgnoreCase))) names.Add(saved + " (not connected)");
            AudioOutputPicker.ItemsSource = names;
            AudioOutputPicker.SelectedItem = saved == null ? DefaultOutputLabel
                : names.FirstOrDefault(n => n.Equals(saved, StringComparison.OrdinalIgnoreCase)) ?? names[^1];
        }
        catch (Exception ex) { Status("Could not list audio devices: " + ex.Message); }
        finally { loadingOutputs = false; }
    }

    private void AudioOutput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingOutputs || !IsInitialized || AudioOutputPicker.SelectedItem is not string choice) return;
        try
        {
            if (choice == DefaultOutputLabel) { AudioOutputSelection.Save(null); Status("Voice will play on the Windows default output."); }
            else if (!choice.EndsWith("(not connected)", StringComparison.Ordinal)) { AudioOutputSelection.Save(choice); Status("Voice will play on " + choice + "."); }
        }
        catch (Exception ex) { Status("Could not save the output choice: " + ex.Message); }
    }
}
