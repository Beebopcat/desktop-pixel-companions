using System.Diagnostics;
using System.IO;
namespace AzerothCompanion;
public partial class MainWindow
{
    private void OpenOcrBoxes()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var script = new[] {
            Path.Combine(AppContext.BaseDirectory, "OcrBoxes", "OCR Boxes.ahk"),
            Path.Combine(root, "Programs", "Desktop Pixel Companions", "OcrBoxes", "OCR Boxes.ahk")
        }.FirstOrDefault(File.Exists);
        var ahk = Path.Combine(root, "Programs", "AutoHotkey", "v2", "AutoHotkey64.exe");
        if (script is null || !File.Exists(ahk)) {
            Notify("OCR Boxes", "Install AutoHotkey v2 and place the OCR Boxes package beside this application.");
            return;
        }
        try {
            var start = new ProcessStartInfo(ahk) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(script)! };
            start.ArgumentList.Add(script);
            Process.Start(start);
            Status("Opened OCR Boxes. Reading starts only when you press Play.");
        } catch (Exception ex) { Notify("OCR Boxes", ex.Message); }
    }
    private void OpenAvatarToolbox()
    {
        var script = new[] {
            Path.Combine(AppContext.BaseDirectory, "AvatarToolbox", "Avatar-Toolbox.ps1"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoHotkey", "Avatar Toolbox", "Avatar-Toolbox.ps1")
        }.FirstOrDefault(File.Exists);
        if (script is null) { Notify("Avatar Toolbox", "The Toolbox script is not part of this installation."); return; }
        try {
            var start = new ProcessStartInfo("conhost.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)! };
            foreach (var argument in new[] { "--headless", "powershell.exe", "-NoProfile", "-STA", "-File", script })
                start.ArgumentList.Add(argument);
            Process.Start(start);
            Status("Opened Avatar Toolbox.");
        } catch (Exception ex) { Notify("Avatar Toolbox", ex.Message); }
    }
}
