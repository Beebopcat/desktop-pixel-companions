using System.IO;
using System.Windows;
using System.Windows.Controls;
using AzerothCompanion.Core;

namespace AzerothCompanion;

internal static class FunctionalTestOptions
{
    internal static bool TryParse(string[] args, out LiveSessionOptions? target, out string error)
    {
        target = null; error = "";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool found = false;
        for (int i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--functional-test")
            {
                if (found) { error = "Duplicate --functional-test."; return false; }
                found = true; continue;
            }
            if (flag is not ("--live-pid" or "--live-process" or "--live-receipt"))
            { error = "Functional test accepts only --functional-test and optional --live-pid, --live-process, --live-receipt."; return false; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            { error = "Missing value for " + flag + "."; return false; }
            if (!values.TryAdd(flag, args[++i]))
            { error = "Duplicate functional test option: " + flag + "."; return false; }
        }
        if (!found) { error = "Missing --functional-test."; return false; }
        if (values.Count == 0) return true;
        if (!values.ContainsKey("--live-pid") || !values.ContainsKey("--live-process"))
        { error = "Supply --live-pid and --live-process together, or omit both for manual selection."; return false; }
        var receipt = values.GetValueOrDefault("--live-receipt") ??
            Path.Combine(AppContext.BaseDirectory, "Verification", "functional-test-session.json");
        // Reuse the existing exact-PID/process validation and its pinned muted route.
        if (!LiveSessionOptions.TryParse(["--live-pid", values["--live-pid"], "--live-process", values["--live-process"],
                "--live-voice", "off", "--live-receipt", receipt], out target, out _, out error)) return false;
        target = target! with { FunctionalTest = true };
        return true;
    }
}

public partial class MainWindow
{
    internal void ConfigureFunctionalTest()
    {
        Title = "Desktop Pixel Companions · static companions · animation review pending";
        AutoDetectCheck.IsChecked = false; AutoDetectCheck.IsEnabled = false;
        SpeechCheck.IsChecked = false; OpenMicCheck.IsChecked = false;
        AutoReadCheck.IsChecked = false; BanterCheck.IsChecked = false;
        ResearchCheck.IsChecked = false; QuestResearchCheck.IsChecked = false;
        IncludeImageCheck.IsChecked = false; CloudConsentCheck.IsChecked = false;
        ProcessingPicker.SelectedIndex = 0;
        GameIdentityText.Text = "Select a visible game window entirely on the primary display. Approved static companions and grass are available; animation review is pending.";
        ReplyText.Text = "Animations remain unapproved and inactive. Choose a game window, then use the conversation controls. Speech and microphone start off.";
        Status("Functional test ready · static companions and grass · capture, speech and mic off.");
    }

    private static IEnumerable<Button> LogicalButtons(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var nested in LogicalButtons(child)) yield return nested;
        }
    }
}
