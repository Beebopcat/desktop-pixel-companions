using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;
public partial class MainWindow
{
    private sealed record VoiceChoice(string Id,string Label);
    private List<VoiceChoice> voiceChoices=new();
    private bool updatingVoicePickers,refreshingVoices;
    private string VoiceFor(string characterId)=>preferences.CharacterVoices.TryGetValue(characterId,out var voice)&&!string.IsNullOrWhiteSpace(voice)?voice:Core.Characters.Find(characterId).DefaultVoiceId;
    private string VoiceLabel(string id)=>voiceChoices.FirstOrDefault(v=>v.Id==id)?.Label??id;
    private void InitializeVoiceChoices()
    {
        preferences.CharacterVoices??=new();
        voiceChoices=Core.Characters.All.Select(p=>new VoiceChoice(p.DefaultVoiceId,p.Name+" · checking availability")).Append(new VoiceChoice(NarrationClient.ApprovedProfile,"Lorath B · approved")).ToList();
        UpdateVoicePickers();
    }
    private void UpdateVoicePickers()
    {
        if(!IsInitialized||PrimaryVoicePicker==null||PartnerVoicePicker==null||voiceChoices.Count==0)return;
        updatingVoicePickers=true;
        try
        {
            void Bind(ComboBox picker,string id)
            {
                var assigned=VoiceFor(id);var items=voiceChoices.ToList();
                if(!items.Any(v=>v.Id==assigned))items.Add(new(assigned,assigned+" · unavailable; choose another"));
                picker.ItemsSource=items;picker.SelectedValue=assigned;
            }
            Bind(PrimaryVoicePicker,character.Id);Bind(PartnerVoicePicker,Partner.Id);
            PrimaryVoiceLabel.Text=character.Name+" voice";PartnerVoiceLabel.Text=Partner.Name+" voice";
            PartnerVoicePicker.IsEnabled=DuoCheck.IsChecked==true;
        }
        finally{updatingVoicePickers=false;}
    }
    private async Task RefreshVoiceChoicesAsync()
    {
        if(refreshingVoices)return;refreshingVoices=true;
        try
        {
            var catalog=await narration.GetVoiceCatalogAsync();if(closed)return;
            voiceChoices=catalog.Select(v=>new VoiceChoice(v.Id,v.Label)).ToList();UpdateVoicePickers();
            VoiceChoicesStatus.Text=$"{voiceChoices.Count} voices · same order as Kokoro Player. Source approval and generated-voice quality are separate. Save preferences to remember assignments.";
        }
        catch(Exception ex){if(!closed)VoiceChoicesStatus.Text="Voice refresh unavailable: "+ex.Message;}
        finally{refreshingVoices=false;}
    }
    private async void RefreshVoices_Click(object sender,RoutedEventArgs e)=>await RefreshVoiceChoicesAsync();
    private void VoiceSelection_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(updatingVoicePickers||!IsInitialized||sender is not ComboBox picker||picker.SelectedItem is not VoiceChoice choice)return;
        var id=ReferenceEquals(picker,PrimaryVoicePicker)?character.Id:Partner.Id;
        if(VoiceFor(id)==choice.Id)return;
        preferences.CharacterVoices[id]=choice.Id;
        if(policy.Running)NewTurn();else CancelOwned();
        Status("Voice assigned to "+(id==character.Id?character.Name:Partner.Name)+": "+choice.Label+". Use Save preferences to retain it.");
    }

    private void AppendReferenceReview(CharacterProfile profile)
    {
        try
        {
            var path=Path.Combine(AppContext.BaseDirectory,"Data","NEW-VOICE-READINESS-20261004.json");
            if(!File.Exists(path))return;
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            var match=document.RootElement.GetProperty("targets").EnumerateArray().FirstOrDefault(item=>item.GetProperty("id").GetString()==profile.Id);
            if(match.ValueKind!=JsonValueKind.Object)return;
            var stack=new StackPanel();
            bool installed=match.TryGetProperty("liveVoiceInstalled",out var live)&&live.ValueKind==JsonValueKind.True;
            bool approved=match.TryGetProperty("userApproved",out var approval)&&approval.ValueKind==JsonValueKind.True;
            stack.Children.Add(new TextBlock{Text=installed?"Installed · source approved · audition status below":approved?"Source approved · installation in progress":"Reference review · refinement pending",FontSize=17,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Accent")});
            stack.Children.Add(new TextBlock{Text=match.GetProperty("sourceTitle").GetString(),FontSize=12,Margin=new Thickness(0,7,0,5),ToolTip=match.GetProperty("sourceUrl").GetString()});
            stack.Children.Add(new TextBlock{Text=match.GetProperty("blocker").GetString(),FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted")});
            if(match.TryGetProperty("candidateTranscript",out var transcript)&&transcript.ValueKind==JsonValueKind.String)
            {
                bool exact=match.TryGetProperty("exactTranscriptVerified",out var verified)&&verified.ValueKind==JsonValueKind.True;
                stack.Children.Add(new TextBlock{Text=exact?"Verified reference words":"Candidate words — exact transcript still unverified",FontSize=11,Margin=new Thickness(0,10,0,4)});
                stack.Children.Add(new TextBox{Text=transcript.GetString(),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=80,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
            }
            var folder=Path.Combine(AppContext.BaseDirectory,"Auditions","ReferenceReview",profile.Id);
            var open=new Button{Content="Open reference audio and review notes",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,10,0,0),IsEnabled=Directory.Exists(folder)};
            open.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});Status("Reference review folder opened. No audio was played automatically.");}catch(Exception error){Status("Could not open reference review: "+error.Message);}};
            stack.Children.Add(open);
            AuditionRows.Children.Add(new Border{Child=stack,Padding=new Thickness(15),Margin=new Thickness(0,0,0,12),CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Color.FromRgb(25,36,52))});
        }
        catch(Exception error) when(error is IOException or JsonException or InvalidOperationException)
        {AuditionRows.Children.Add(new TextBlock{Text="Reference review metadata is unavailable; no pending voice was enabled.",Foreground=(Brush)FindResource("Muted")});}
    }
}
