namespace AzerothCompanion.Services;

public sealed record DirectTextProtectionCheck(int Policy, string Result, string Coverage);
internal static class DirectTextProtection
{
    internal const string Coverage = "Observed user-excluded Albion app ID/executables/install root plus local browser/editor/launcher/tool deny rules; not comprehensive anti-cheat detection.";
    internal static string? BlockReason(string? steamAppId, string? executablePath)
    {
        if (steamAppId == "761890") return "Albion Online was explicitly excluded by the user; no attachment or calibration is permitted.";
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        string leaf = Path.GetFileName(executablePath);
        if (new[] { "Albion-Online.exe", "Albion-Online_BE.exe", "AlbionLauncher.exe" }.Contains(leaf, StringComparer.OrdinalIgnoreCase)) return "User-excluded Albion executable; no attachment or calibration is permitted.";
        const string observedRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Albion Online";
        if (executablePath.Equals(observedRoot, StringComparison.OrdinalIgnoreCase) || executablePath.StartsWith(observedRoot + "\\", StringComparison.OrdinalIgnoreCase)) return "User-excluded Albion installation path; no attachment or calibration is permitted.";
        return null;
    }
    internal static DirectTextProtectionCheck Review(DirectTextProfile p)
    {
        string? reason = BlockReason(p.SteamAppId, p.ExecutablePath);
        if (reason != null) throw new InvalidDataException(reason);
        if (p.Distribution == "Local" && LocalGameProfileStore.IsToolExecutable(Path.GetFileNameWithoutExtension(p.ExecutablePath))) throw new InvalidDataException("Local browser/editor/launcher/tool cannot be calibrated as a game.");
        return new(1, "no-known-exclusion", Coverage);
    }
}
