using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AzerothCompanion.Services;

namespace AzerothCompanion;

/// <summary>Quality of Life: three global hotkeys that polish up to 1,200 selected characters in any text box, using a local model.</summary>
public partial class MainWindow
{
    private static readonly (string Name, PolishMode Mode, int Id, string Default)[] QolItems =
    [
        ("Polish up text", PolishMode.Polish, 41020, "Ctrl+Alt+1"),
        ("Polish, rephrase, and create a prompt", PolishMode.Prompt, 41021, "Ctrl+Alt+2"),
        ("Polish, rephrase, and improve text formatting", PolishMode.Format, 41022, "Ctrl+Alt+3")
    ];
    private readonly TextPolishService polish = new();
    private readonly TextBox[] qolBoxes = new TextBox[3];
    private readonly TextBlock[] qolStatus = new TextBlock[3];
    private HwndSource? qolSource;
    private bool qolBusy;

    private void InitializeQualityOfLife()
    {
        if (preferences.PolishHotkeys is not { Length: 3 }) preferences.PolishHotkeys = QolItems.Select(i => i.Default).ToArray();
        BuildQolPanel();
        SourceInitialized += (_, _) =>
        {
            qolSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            qolSource?.AddHook(QolHook);
            for (int i = 0; i < QolItems.Length; i++) RegisterQol(i);
        };
        Closed += (_, _) => { for (int i = 0; i < QolItems.Length; i++) UnregisterHotKey(new WindowInteropHelper(this).Handle, QolItems[i].Id); };
    }

    private void BuildQolPanel()
    {
        var muted = (Brush)FindResource("Muted");
        QolPanel.Children.Add(new TextBlock
        {
            Text = "Select up to 1,200 characters in any text box - an app, a game or a web page - press a shortcut, and the polished text replaces your selection. Ctrl+Z in that app undoes it. It runs on your local model; nothing leaves this PC.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = muted, Margin = new Thickness(0, 0, 0, 8)
        });
        for (int i = 0; i < QolItems.Length; i++)
        {
            int index = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            name.Children.Add(new TextBlock { Text = QolItems[i].Name, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            qolStatus[i] = new TextBlock { FontSize = 10.5, Foreground = muted, TextWrapping = TextWrapping.Wrap };
            name.Children.Add(qolStatus[i]);
            var box = new TextBox { IsReadOnly = true, Cursor = Cursors.Hand, Text = preferences.PolishHotkeys[i], ToolTip = "Click here, then press the new shortcut", VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 5, 6, 5), IsReadOnlyCaretVisible = false };
            box.PreviewKeyDown += (_, e) => CaptureQolKey(index, e);
            qolBoxes[i] = box; Grid.SetColumn(box, 1);
            var reset = new Button { Content = "Reset", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
            reset.Click += (_, _) => SetQol(index, QolItems[index].Default);
            Grid.SetColumn(reset, 2);
            row.Children.Add(name); row.Children.Add(box); row.Children.Add(reset);
            QolPanel.Children.Add(row);
        }
        var modelRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        modelRow.Children.Add(new TextBlock { Text = "Model (Ollama)", FontSize = 11, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var model = new TextBox { Text = preferences.PolishModel, MinWidth = 220, Padding = new Thickness(6, 4, 6, 4), ToolTip = "Any model installed in Ollama. Smaller = faster." };
        model.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(model.Text)) { preferences.PolishModel = model.Text.Trim(); preferences.Save(); } };
        modelRow.Children.Add(model);
        QolPanel.Children.Add(modelRow);
    }

    private void CaptureQolKey(int index, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab or Key.None) return;
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
        var text = HotkeySpec.Describe(Keyboard.Modifiers, key);
        if (HotkeySpec.Parse(text) == null) { qolStatus[index].Text = "Use at least one of Ctrl / Alt / Shift / Win with a key."; return; }
        SetQol(index, text);
    }

    private void SetQol(int index, string text)
    {
        preferences.PolishHotkeys[index] = text;
        qolBoxes[index].Text = text;
        preferences.Save();
        RegisterQol(index);
    }

    private void RegisterQol(int index)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        UnregisterHotKey(hwnd, QolItems[index].Id);
        var spec = HotkeySpec.Parse(preferences.PolishHotkeys[index]);
        if (spec == null) { qolStatus[index].Text = "No shortcut set."; return; }
        bool registered = RegisterHotKey(hwnd, QolItems[index].Id, spec.Value.Modifiers | HotkeySpec.NoRepeat, spec.Value.VirtualKey);
        QolLog("register " + preferences.PolishHotkeys[index] + " -> " + registered + " (err " + Marshal.GetLastWin32Error() + ")");
        qolStatus[index].Text = registered
            ? "Ready" : "That shortcut is used by another app - choose a different one.";
    }

    private static void QolLog(string line)
    {
        try
        {
            var dir = Core.Preferences.StateDirectory; System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "quality-of-life.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
        }
        catch { }
    }

    private nint QolHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312) QolLog("WM_HOTKEY id=" + (int)wParam);
        if (msg == 0x0312)
        {
            int id = (int)wParam;
            for (int i = 0; i < QolItems.Length; i++)
                if (QolItems[i].Id == id) { handled = true; int index = i; Dispatcher.BeginInvoke(async () => await RunQolAsync(index)); }
        }
        return 0;
    }

    private void Notify(string title, string text)
    {
        Status(text);
        try { tray?.ShowBalloonTip(2500, title, text, System.Windows.Forms.ToolTipIcon.Info); } catch { }
    }

    private async Task RunQolAsync(int index)
    {
        QolLog("run " + index + " busy=" + qolBusy);
        if (qolBusy) { Notify("Still working", "The previous polish is still running."); return; }
        qolBusy = true;
        string? original = null; bool clipboardTouched = false;
        try
        {
            var target = SelectionSwap.Foreground;
            QolLog("foreground=" + target + " own=" + SelectionSwap.IsOwnWindow(target));
            if (target == 0 || SelectionSwap.IsOwnWindow(target)) { Notify(QolItems[index].Name, "Click into the text you want to polish first."); return; }
            await SelectionSwap.WaitForModifierRelease();
            original = SelectionSwap.TryGetText();
            clipboardTouched = true;
            var selected = await SelectionSwap.CopySelectionAsync();
            QolLog("copied chars=" + (selected?.Length ?? -1));
            if (string.IsNullOrEmpty(selected)) { Notify(QolItems[index].Name, "No text was selected (or this app blocks copying)."); return; }
            if (selected.Length > TextPolishService.MaxCharacters)
            { Notify(QolItems[index].Name, $"The selection is {selected.Length:N0} characters; the limit is {TextPolishService.MaxCharacters:N0}. Select a shorter piece."); return; }
            Notify(QolItems[index].Name, "Polishing with your local model…");
            SetVisual("thinking");
            string result;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(170)))
                result = await polish.RunAsync(QolItems[index].Mode, selected, preferences.PolishModel, cts.Token);
            QolLog("model returned chars=" + result.Length);
            if (SelectionSwap.Foreground != target)
            {
                SelectionSwap.TrySetText(result); original = null; clipboardTouched = false;
                Notify(QolItems[index].Name, "You switched windows, so the result was copied to the clipboard instead - press Ctrl+V.");
                return;
            }
            if (!SelectionSwap.TrySetText(result)) { Notify(QolItems[index].Name, "Could not use the clipboard. Try again."); return; }
            SelectionSwap.Paste();
            await Task.Delay(450);
            Status($"{QolItems[index].Name}: done ({selected.Length:N0} → {result.Length:N0} characters). Ctrl+Z undoes it.");
        }
        catch (Exception ex) { QolLog("error: " + ex.Message); Notify(QolItems[index].Name, ex.Message); }
        finally
        {
            if (clipboardTouched && original != null) SelectionSwap.TrySetText(original);
            SetVisual("idle");
            qolBusy = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);

    // ---- Repair tools: opens the Jaina repair widget as its own window.
    internal void OpenRepairLauncher() => OpenAvatarToolbox();

    private void RepairTop_Click(object sender, RoutedEventArgs e) => OpenRepairLauncher();
}
