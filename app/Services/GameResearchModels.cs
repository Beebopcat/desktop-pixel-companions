using System.Text;
using System.Text.RegularExpressions;

namespace AzerothCompanion.Services;

public enum GameResearchStatus { Ready, Cached, Disabled, Unavailable, Throttled, Busy, Stale }

/// <summary>A local metadata candidate, not proof of a game's identity or edition.</summary>
public sealed record GameResearchIdentity(string Name, string WindowTitle, string ProcessName,
    bool IsUserOverride, bool IsRecognized, string Evidence)
{
    private static readonly Dictionary<string, string> KnownProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wow"] = "World of Warcraft", ["wowclassic"] = "World of Warcraft Classic",
        ["diablo iv"] = "Diablo IV", ["diabloiv"] = "Diablo IV", ["diablo iii"] = "Diablo III",
        ["eldenring"] = "Elden Ring", ["skyrimse"] = "The Elder Scrolls V: Skyrim",
        ["skyrim"] = "The Elder Scrolls V: Skyrim", ["bg3"] = "Baldur's Gate 3",
        ["bg3_dx11"] = "Baldur's Gate 3", ["ffxiv_dx11"] = "Final Fantasy XIV",
        ["ffxiv"] = "Final Fantasy XIV", ["cyberpunk2077"] = "Cyberpunk 2077",
        ["witcher3"] = "The Witcher 3: Wild Hunt", ["rdr2"] = "Red Dead Redemption 2",
        ["fallout4"] = "Fallout 4", ["fallout76"] = "Fallout 76", ["starfield"] = "Starfield",
        ["horizonforbiddenwest"] = "Horizon Forbidden West", ["horizonzerodawn"] = "Horizon Zero Dawn",
        ["gw2-64"] = "Guild Wars 2", ["eso64"] = "The Elder Scrolls Online",
        ["destiny2"] = "Destiny 2", ["warframe.x64"] = "Warframe",
        ["minecraft.windows"] = "Minecraft", ["valheim"] = "Valheim", ["terraria"] = "Terraria"
    };

    public static GameResearchIdentity Identify(string? windowTitle, string? processName, string? userOverride = null)
    {
        var title = Clean(windowTitle, 240);
        var process = Clean(processName, 96);
        var specified = Clean(userOverride, 160);
        if (specified.Length > 0)
            return new(specified, title, process, true, true, "Game name supplied by the user.");
        var stem = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
        if (KnownProcesses.TryGetValue(stem, out var known))
            return new(known, title, process, false, true, "Known process name; game identity and edition still need confirmation.");
        var candidate = Clean(title.Length > 0 ? title : stem, 160);
        return new(candidate, title, process, false, false, "Window metadata candidate; identity is unverified. A game-name override improves research.");
    }

    internal static string Clean(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var clean = Regex.Replace(value, @"\s+", " ").Trim();
        clean = new string(clean.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= maximum ? clean : clean[..maximum];
    }
}

/// <summary>Only explicit bounded text. Deliberately has no screenshot, OCR, audio, or file inputs.</summary>
public sealed record GameResearchRequest(GameResearchIdentity Game, string Topic = "", string Progress = "",
    bool AllowSpoilers = false);

public sealed record GameResearchSource(int Number, string Title, string Url, DateTimeOffset RetrievedUtc);

public sealed record GameResearchResult(GameResearchStatus Status, string Message, string Synthesis = "",
    string GameContext = "", IReadOnlyList<GameResearchSource>? Sources = null,
    decimal AccountedUsd = 0, BudgetSnapshot? Budget = null, DateTimeOffset? RetrievedUtc = null,
    DateTimeOffset? RetryAtUtc = null)
{
    public bool HasContext => Status is GameResearchStatus.Ready or GameResearchStatus.Cached && GameContext.Length > 0;
}
