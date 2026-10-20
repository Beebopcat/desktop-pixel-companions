using System.IO;
using System.Text.Json;
namespace AzerothCompanion.Core;
public sealed class Preferences
{
    public string CharacterId {get;set;}="sylvanas";
    public string SecondCharacterId {get;set;}="jaina";
    public bool Duo {get;set;}=true;
    public double OverlayFraction {get;set;}=.04;
    public double AvatarScale {get;set;}=.55;
    public string Flooring {get;set;}="Grass";
    public string[] PolishHotkeys {get;set;}=["Ctrl+Alt+1","Ctrl+Alt+2","Ctrl+Alt+3"];
    public string PolishModel {get;set;}="qwen3:4b-instruct";
    public string GameContext {get;set;}="English; spoiler-free. Ask for game version and my progress before specific build or quest recommendations.";
    public bool Spoilers {get;set;}
    public double PortraitSize {get;set;}=290;
    public Dictionary<string,string> CharacterVoices {get;set;}=new(StringComparer.Ordinal);
    public DialogueRoleVoices DialogueRoles {get;set;}=new();
    public static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AzerothCompanion");
    public static Preferences Load(){try{return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(StateDirectory,"preferences.json")))??new();}catch{return new();}}
    public void Save(){Directory.CreateDirectory(StateDirectory);var p=Path.Combine(StateDirectory,"preferences.json");File.WriteAllText(p+".tmp",JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(p+".tmp",p,true);}
}
