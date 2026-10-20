using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AzerothCompanion.Core;

public enum FreeGameResearchStatus { Ready, Partial, Cached, Unavailable, Throttled, Stale }
public sealed record GameKnowledgeSource(int Number, string Title, string Url, DateTimeOffset RetrievedUtc,
    string Kind, string Excerpt, string Topic = "", string Reliability = "");
public sealed record FreeGameResearchResult(FreeGameResearchStatus Status, string Context,
    IReadOnlyList<GameKnowledgeSource> Sources, string Message, DateTimeOffset RetrievedUtc,
    DateTimeOffset? RetryAtUtc = null, string? CanonicalTitle = null, bool IdentityVerified = false,
    IReadOnlyList<GameResearchCoverage>? Coverage = null)
{
    public bool HasContext => Status is FreeGameResearchStatus.Ready or FreeGameResearchStatus.Partial or FreeGameResearchStatus.Cached && Context.Length > 0;
}
public sealed record GameResearchCoverage(string Id,string Label,string Status,int Sources,DateTimeOffset? RetrievedUtc);
public sealed record GameResearchTopic(string Id,string Label,string Query)
{
    public static readonly IReadOnlyList<GameResearchTopic> Automatic = new[] {
        new GameResearchTopic("overview","Overview and identity",""),
        new GameResearchTopic("lore","Lore and world","lore world setting history"),
        new GameResearchTopic("characters","Characters, factions and classes","characters factions classes roles"),
        new GameResearchTopic("systems","Systems and builds","gameplay systems mechanics builds strategy"),
        new GameResearchTopic("quests","Quests and choices","quests choices consequences walkthrough"),
        new GameResearchTopic("theories","Theories and connections","fan theories connections interpretation speculation")
    };
    public static GameResearchTopic For(string query)=>Automatic.FirstOrDefault(topic=>topic.Query==query)??
        new("custom-"+GameKnowledgeStore.Key("topic",query,null)[..12],"Requested public topic",query);
}
public sealed record GameDossierEntry(GameResearchTopic Topic,FreeGameResearchResult Result);
public sealed record GameResearchDossier(int Schema,string Title,string? StoreId,string CanonicalTitle,
    bool IdentityVerified,DateTimeOffset StartedUtc,int RequestsUsed,IReadOnlyList<GameDossierEntry> Entries);
public sealed record GameResearchRequestBudget(DateTimeOffset StartedUtc,int Requests);

public static class GameKnowledgeText
{
    public static string ValidateTitle(string? value)
    {
        var title = Clean(value, 160);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || title.Length < 2 ||
            value.Any(char.IsControl) || title.Contains('@') || title.Contains("://", StringComparison.Ordinal) ||
            title.Contains('\\') || title.Contains('/') || !title.Any(char.IsLetter))
            throw new ArgumentException("Supply a game title of 2–160 characters, without URLs or private paths.", nameof(value));
        return title;
    }
    public static string ValidateTopic(string? value)
    {
        if (value is null) return "";
        if (value.Length > 300 || value.Any(char.IsControl) || value.Contains('@') || value.Contains("://", StringComparison.Ordinal) || value.Contains('\\'))
            throw new ArgumentException("Research topics must be bounded public game questions, without URLs or private paths.", nameof(value));
        return Clean(value, 300);
    }
    public static string Clean(string? value, int maximum)
    {
        var plain = WebUtility.HtmlDecode(Regex.Replace(value ?? "", "<[^>]{0,2000}>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        plain = Regex.Replace(plain, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        plain = new string(plain.Where(character => !char.IsControl(character)).ToArray());
        return plain.Length <= maximum ? plain : plain[..maximum];
    }
    public static string MatchKey(string value) => new(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static bool SameGame(string expected, string candidate)
    {
        var key = MatchKey(expected);
        var other = MatchKey(candidate.Replace("(video game)", "", StringComparison.OrdinalIgnoreCase));
        return key == other || key.Length >= 6 && other.StartsWith(key, StringComparison.Ordinal) &&
            other[key.Length..] is "remastered" or "definitiveedition" or "gameoftheyearedition" or "completeedition";
    }
    public static bool IsPublicSourceUrl(string? value) => value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns &&
        uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
        !uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) && uri.Port == 443;
}
