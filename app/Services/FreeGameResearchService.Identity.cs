using AzerothCompanion.Core;
namespace AzerothCompanion.Services;

public sealed partial class FreeGameResearchService
{
    // Exact Steam app IDs plus both observed titles. These exceptions never apply to wiki/search identity.
    private static bool SteamTitleMatches(string appId, string installedTitle, string officialTitle) =>
        GameKnowledgeText.SameGame(installedTitle, officialTitle) || (appId, GameKnowledgeText.MatchKey(installedTitle), GameKnowledgeText.MatchKey(officialTitle)) switch
        {
            ("2374160", "windymeadowaroadwardentaledemo", "windymeadowremasterdemo") => true,
            ("382900", "finalfantasyvi", "finalfantasyvioldver") => true,
            _ => false
        };

    private static string SteamIdentityNote(string appId, string installedTitle, string officialTitle)
    {
        if (!SteamTitleMatches(appId, installedTitle, officialTitle)) return "";
        if (appId == "2374160" && GameKnowledgeText.MatchKey(officialTitle) == "windymeadowremasterdemo")
            return "Installed title: " + installedTitle + ". Current official Steam app 2374160 title: " + officialTitle +
                ". This title change does not verify the installed demo revision or remaster content. ";
        if (appId == "382900" && GameKnowledgeText.MatchKey(officialTitle) == "finalfantasyvioldver")
            return "Edition identity: Steam app 382900 is FINAL FANTASY VI (Old ver.), the old Steam edition. " +
                "General FINAL FANTASY VI sources may describe shared lore; Pixel Remaster content and edition-specific mechanics remain unverified. ";
        return "";
    }

    private static bool RequiresCurrentSteamIdentity(string? appId, string title) => appId switch
    {
        "2374160" => GameKnowledgeText.MatchKey(title) is "windymeadowaroadwardentaledemo" or "windymeadowremasterdemo",
        "382900" => GameKnowledgeText.MatchKey(title) is "finalfantasyvi" or "finalfantasyvioldver",
        _ => false
    };
    private static bool CurrentSteamIdentity(FreeGameResearchResult result, string title, string? appId)
    {
        if (!RequiresCurrentSteamIdentity(appId, title)) return true;
        string marker = appId == "382900" ? "Edition identity: Steam app 382900 is FINAL FANTASY VI (Old ver.)" : "Current official Steam app 2374160 title: Windy Meadow Remaster Demo";
        return result.IdentityVerified && result.Sources.Any(source => source.Url == "https://store.steampowered.com/app/" + appId + "/" &&
            source.Kind.Contains("official", StringComparison.Ordinal) && source.Excerpt.Contains(marker, StringComparison.Ordinal));
    }
    private static GameResearchDossier ReviewLoadedIdentity(GameResearchDossier dossier)
    {
        if (!RequiresCurrentSteamIdentity(dossier.StoreId, dossier.Title) ||
            dossier.Entries.Any(entry => CurrentSteamIdentity(entry.Result, dossier.Title, dossier.StoreId))) return dossier;
        // Retain this cache window and spent requests. Old wiki-only identity/unavailable entries need re-verification.
        return dossier with { IdentityVerified = false, CanonicalTitle = dossier.Title, Entries = [] };
    }
}
