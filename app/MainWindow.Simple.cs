using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AzerothCompanion;

/// <summary>Basic view by default: three big buttons and the essentials. Research, Character review and Settings stay one switch away.</summary>
public partial class MainWindow
{
    private void AdvancedTabs_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var visibility = AdvancedTabsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ResearchTab.Visibility = CharacterReviewTab.Visibility = SettingsTab.Visibility = visibility;
    }

    private static object IconButtonContent(string icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", icon + ".png");
            if (System.IO.File.Exists(path))
                row.Children.Add(new Image { Source = new BitmapImage(new Uri(path)), Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0) });
        }
        catch { }
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        return row;
    }

    private bool CompanionsVisible => overlay != null && overlay.IsVisible;

    /// <summary>--window-pos=X,Y opens the window at that screen position (used to keep test runs off the main display).</summary>
    private void ApplyLaunchPlacement()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
            if (arg.StartsWith("--window-pos=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = arg[13..].Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y))
                { WindowStartupLocation = WindowStartupLocation.Manual; Left = x; Top = y; }
            }
    }

    private void InitializeToolbar()
    {
        ApplyLaunchPlacement();
        ReadNowTopButton.Content = IconButtonContent("read", "Read screen now");
        ResearchTopButton.Content = IconButtonContent("research", "Research");
        RepairTopButton.Content = IconButtonContent("repair", "Repair tools");
        UpdateToolbar();
    }

    internal void UpdateToolbar() => ToggleCompanionsButton.Content = IconButtonContent("companions", CompanionsVisible ? "Companions: on (click to hide)" : "Companions: off (click to show)");

    internal void ToggleCompanions()
    {
        if (CompanionsVisible) overlay?.Hide(); else PopOut_Click(this, new RoutedEventArgs());
        UpdateToolbar();
    }

    internal void ShowResearch()
    {
        RestoreFromTray();
        AdvancedTabsCheck.IsChecked = true;
        ResearchTab.Visibility = Visibility.Visible;
        ResearchTab.IsSelected = true;
        ResearchTopicBox.Focus();
    }

    private void ToggleCompanions_Click(object sender, RoutedEventArgs e) => ToggleCompanions();
    private async void ReadNowTop_Click(object sender, RoutedEventArgs e) => await ReadOnceAsync();
    private void ResearchTop_Click(object sender, RoutedEventArgs e) => ShowResearch();
}
