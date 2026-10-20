using System.Windows;
using Microsoft.Win32;

namespace AzerothCompanion;

/// <summary>Minimizing sends the window to the notification area (system tray); the companions, timers and services keep running.</summary>
public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? tray, trayCompanions, trayRead, trayResearch;
    private bool trayHintShown, exitingFromTray;

    private void InitializeTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Desktop Pixel Companions", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Show / hide desktop companions", null, (_, _) => Dispatcher.Invoke(() => ToggleCompanions()));
        menu.Items.Add("Read my screen now", null, (_, _) => Dispatcher.Invoke(async () => await ReadOnceAsync()));
        menu.Items.Add("OCR Boxes...", null, (_, _) => Dispatcher.Invoke(OpenOcrBoxes));
        menu.Items.Add("Avatar Toolbox...", null, (_, _) => Dispatcher.Invoke(OpenRepairLauncher));
        menu.Items.Add("Quiet (stop speaking)", null, (_, _) => Dispatcher.Invoke(() => Quiet_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));
        System.Drawing.Icon? icon = null;
        try { if (Environment.ProcessPath is { } exe) icon = System.Drawing.Icon.ExtractAssociatedIcon(exe); } catch { }
        tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "Desktop Pixel Companions",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(RestoreFromTray); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };

        // Three simple one-click icons next to the clock.
        trayCompanions = MakeShortcutIcon("companions", "Companions: show / hide", () => ToggleCompanions());
        trayRead = MakeShortcutIcon("read", "Read my screen now", async () => await ReadOnceAsync());
        trayResearch = MakeShortcutIcon("research", "Research", () => ShowResearch());
        var promote = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        promote.Tick += (_, _) => { promote.Stop(); PromoteTrayIcons(); };
        promote.Start();
    }

    private System.Windows.Forms.NotifyIcon MakeShortcutIcon(string file, string tip, Action action)
    {
        System.Drawing.Icon? icon = null;
        try { icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", file + ".ico")); } catch { }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Desktop Pixel Companions", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));
        var item = new System.Windows.Forms.NotifyIcon { Icon = icon ?? System.Drawing.SystemIcons.Application, Text = tip, ContextMenuStrip = menu, Visible = true };
        item.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(action); };
        return item;
    }

    /// <summary>Windows 11 hides new tray icons in the overflow flyout. Mark this app's icons "shown" so they sit on the taskbar.</summary>
    private static void PromoteTrayIcons()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true);
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, true);
                if (key?.GetValue("ExecutablePath") is string path && string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (!trayHintShown && tray != null)
        {
            trayHintShown = true;
            tray.ShowBalloonTip(3000, "Still running", "Desktop Pixel Companions is in the tray. Click the icon to open it, or right-click for options.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        exitingFromTray = true;
        Close();
        Application.Current.Shutdown();
    }

    private void DisposeTray()
    {
        if (tray == null) return;
        foreach (var icon in new[] { tray, trayCompanions, trayRead, trayResearch }) { if (icon == null) continue; icon.Visible = false; icon.Dispose(); }
        tray = trayCompanions = trayRead = trayResearch = null;
    }
}
