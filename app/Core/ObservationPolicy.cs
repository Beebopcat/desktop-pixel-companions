using System.Text.RegularExpressions;
namespace AzerothCompanion.Core;
public sealed class ObservationPolicy
{
    private string candidate="", lastSpoken=""; private DateTimeOffset candidateSince=DateTimeOffset.MinValue;
    public static string Normalize(string text)=>Regex.Replace(text.Trim(),@"\s+"," ");
    public string? Observe(string text,DateTimeOffset now)
    {
        var normal=Normalize(text);
        if(normal.Length<3){candidate="";return null;}
        if(normal!=candidate){candidate=normal;candidateSince=now;return null;}
        if(now-candidateSince<TimeSpan.FromMilliseconds(900)||normal==lastSpoken)return null;
        lastSpoken=normal;return text.Trim();
    }
    public void Reset(){candidate="";lastSpoken="";candidateSince=DateTimeOffset.MinValue;}
}
public sealed class SessionPolicy
{
    public bool Running {get;set;}
    public bool Quiet {get;set;}
    public bool DialogueOrCombat {get;set;}
    public DateTimeOffset LastBanter {get;set;}=DateTimeOffset.MinValue;
    public bool CanBanter(DateTimeOffset now)=>Running&&!Quiet&&!DialogueOrCombat&&now-LastBanter>=TimeSpan.FromSeconds(90);
    public bool AllowCloud(bool hybrid,bool consent)=>Running&&hybrid&&consent;
}
