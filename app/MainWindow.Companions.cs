using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using AzerothCompanion.Core;
namespace AzerothCompanion;
public partial class MainWindow
{
    private readonly SessionConversation conversation=new();
    private string? activeSpeaker;
    private CharacterProfile Partner=>PartnerPicker.SelectedItem as CharacterProfile??Characters.All.First(x=>x.Id!=character.Id);
    private void InitializePartners()
    {
        PartnerPicker.ItemsSource=Characters.All;
        PartnerPicker.SelectedItem=Characters.Find(preferences.SecondCharacterId);
        DuoCheck.IsChecked=preferences.Duo;
        FootprintSlider.Value=Math.Clamp(preferences.OverlayFraction*100,1.5,4.5);
        AvatarScaleSlider.Value=Math.Clamp(preferences.AvatarScale*100,15,100);
        UpdatePartner();
    }
    private void Partner_Changed(object sender,RoutedEventArgs e){if(!IsInitialized||PartnerPicker==null)return;UpdatePartner();}
    private void PartnerSelection_Changed(object sender,SelectionChangedEventArgs e)=>Partner_Changed(sender,e);
    private void UpdatePartner()
    {
        if(PartnerPicker.SelectedItem is CharacterProfile p&&p.Id==character.Id)
            PartnerPicker.SelectedItem=Characters.All.First(x=>x.Id!=character.Id);
        PartnerPicker.IsEnabled=DuoCheck.IsChecked==true;
        if(policy.Running)NewTurn();
        overlay?.SetPartner(Partner,DuoCheck.IsChecked==true);
        UpdateVoicePickers();
    }
    private void Footprint_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(!IsInitialized||FootprintLabel==null)return;
        FootprintLabel.Text=$"Maximum area: {FootprintSlider.Value:F2}% of the display";
        overlay?.ConfigureFootprint(selectedWindow?.Handle??0,FootprintSlider.Value/100);
    }
    private void AvatarScale_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(!IsInitialized||AvatarScaleLabel==null)return;
        preferences.AvatarScale=AvatarScaleSlider.Value/100;
        AvatarScaleLabel.Text=$"Avatar width and height: {AvatarScaleSlider.Value:F0}%";
        overlay?.SetAvatarScale(preferences.AvatarScale);
    }
    private string CompanionPersona()=>DuoCheck.IsChecked!=true?character.Persona:
        character.Persona+$"\nThis session has two companions: {character.Name} and {Partner.Name}. " +
        $"The second companion's manner is: {Partner.Tone}. Provide a short natural exchange of at most two lines, " +
        $"each prefixed exactly '{character.Name}:' or '{Partner.Name}:'. Let the second add something useful, not repeat. " +
        "Answer the user together; do not invent ongoing events or manufacture a conversation loop. " +
        "Other games and ordinary small talk are welcome when the user asks. Speak plainly while keeping each personality.";
    private async Task PresentConversationAsync(string text,CancellationToken token)
    {
        var ownerGeneration=generation;
        ReplyText.Text=text;overlay?.SetState("idle",text);
        var parts=new List<(string Id,string Text)>();
        foreach(var line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            var selected=new[]{character}.Concat(DuoCheck.IsChecked==true?new[]{Partner}:Array.Empty<CharacterProfile>());
            var matched=selected.FirstOrDefault(p=>line.TrimStart().StartsWith(p.Name+":",StringComparison.OrdinalIgnoreCase));
            var content=matched==null?line:line[(line.IndexOf(':')+1)..].Trim();
            parts.Add((matched?.Id??character.Id,content));
        }
        try{foreach(var part in parts)
        {
            token.ThrowIfCancellationRequested();activeSpeaker=part.Id;
            overlay?.SetSpeaker(part.Id,"idle",Characters.Find(part.Id).Name+": "+part.Text);
            await SpeakAsync(part.Text,token);
        }}finally{if(ownerGeneration==generation)activeSpeaker=null;}
    }
}
