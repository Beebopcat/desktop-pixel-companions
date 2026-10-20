using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AzerothCompanion.Core;
using AzerothCompanion.Services;
using Microsoft.Win32;

namespace AzerothCompanion;

public partial class MainWindow
{
    private readonly GameTextService gameText=new();
    private readonly DialogueWatchPolicy dialogueWatch=new();
    private GameTextOptions gameTextOptions=new();
    private readonly Dictionary<string,string> dialogueSpeakerVoices=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> dialogueSpeakerAvatars=new(StringComparer.Ordinal);
    private readonly DialogueSpeechQueue dialogueSpeechQueue=new();
    private readonly SemaphoreSlim dialogueDrainGate=new(1,1);
    private bool dialogueSpeakerOverrides,dialogueRoleOverrides,dialogueDrainFault;
    private GameTextObservation? latestGameObservation;
    private DateTimeOffset? latestObservationAt;
    private void InitializeGameText(){preferences.DialogueRoles??=new();Closed+=(_,_)=>gameText.Dispose();}
    private void ResetGameTextSession()
    {
        if(!closed)gameText.Reset();gameTextOptions=new();dialogueSpeakerVoices.Clear();dialogueSpeakerAvatars.Clear();dialogueWatch.Reset();
        dialogueSpeakerOverrides=false;dialogueRoleOverrides=false;dialogueDrainFault=false;
        dialogueSpeechQueue.Cancel("Game text session ended.",resetSession:true);UpdateDialogueQueueStatus();
        latestGameObservation=null;latestObservationAt=null;
    }
    private IReadOnlyList<DialogueVoiceRoute> RoutesFor(GameTextObservation observation)=>DialogueVoiceRouter.Route(observation,
        dialogueSpeakerVoices,VoiceFor(character.Id),preferences.DialogueRoles,dialogueSpeakerOverrides,dialogueRoleOverrides);
    private string DialogueAvatar(string? speaker)=>DialogueVoiceRouter.AvatarForSpeaker(speaker,dialogueSpeakerAvatars,
        character.Id,DuoCheck.IsChecked==true?Partner.Id:null,dialogueSpeakerOverrides);
    private bool HasPendingDialogue=>dialogueSpeechQueue.HasPending;
    private void UpdateDialogueQueueStatus(string? notice=null)
    {
        if(closed||DialogueQueueStatusText==null)return;
        var summary=dialogueSpeechQueue.Summary();
        var counts=$"Backlog {summary.Pending} · played {summary.Delivered} · text-only {summary.TextOnly}"+
            (summary.Skipped>0?$" · skipped {summary.Skipped}":"")+(summary.Cancelled>0?$" · cancelled {summary.Cancelled}":"");
        DialogueQueueStatusText.Text=notice==null?counts:notice+" "+counts;
    }
    private void CancelDialogueQueue(string reason)
    {dialogueSpeechQueue.Cancel(reason);dialogueDrainFault=false;UpdateDialogueQueueStatus(reason);}
    private DialogueAcceptance AcceptDirectDialogue(GameTextObservation observation,bool narrationRequested)
    {
        var accepted=dialogueSpeechQueue.Accept(observation,RoutesFor(observation),narrationRequested&&!dialogueDrainFault,
            DateTimeOffset.UtcNow,dialogueDrainFault?"Narration paused after a renderer failure; text only, no automatic replay.":null);
        UpdateDialogueQueueStatus(accepted.Notice??(dialogueDrainFault?"Narration paused after a renderer failure. Choose Read once or change speech settings to retry.":
            accepted.TextOnly>0?$"{accepted.TextOnly} event(s) received as text only; no later automatic replay.":null));
        return accepted;
    }
    private async Task DrainDialogueQueueAsync(CancellationToken ct)
    {
        if(dialogueDrainFault||!await dialogueDrainGate.WaitAsync(0,ct))return;
        var owner=generation;var previousSpeaker=activeSpeaker;DialogueDelivery? current=null;
        try
        {
            while(!closed&&owner==generation&&policy.Running&&!policy.Quiet&&dialogueSpeechQueue.TryBegin(out current))
            {
                ct.ThrowIfCancellationRequested();activeSpeaker=DialogueAvatar(current!.Route.SpeakerName);UpdateDialogueQueueStatus();
                var result=await SpeakAsync(current.Route.Text,ct,current.Route.VoiceId);
                if(owner!=generation)return;
                dialogueSpeechQueue.Complete(current,result,result==DialoguePlaybackResult.Completed?null:"Literal event was not fully played; no automatic retry.");
                if(result is DialoguePlaybackResult.Cancelled or DialoguePlaybackResult.Skipped)
                {dialogueSpeechQueue.Cancel("Remaining dialogue cancelled after an incomplete utterance.");UpdateDialogueQueueStatus("Dialogue skipped/cancelled after an incomplete utterance; no automatic retry.");break;}
                current=null;UpdateDialogueQueueStatus();
            }
        }
        catch(OperationCanceledException)
        {if(current!=null)dialogueSpeechQueue.Complete(current,DialoguePlaybackResult.Cancelled,"Owned dialogue cancelled.");if(owner==generation)dialogueSpeechQueue.Cancel("Owned dialogue cancelled.");}
        catch(Exception error)
        {
            if(current!=null)dialogueSpeechQueue.Complete(current,DialoguePlaybackResult.Skipped,error.Message);
            if(owner==generation){dialogueSpeechQueue.Cancel("Narration paused after a renderer failure; no automatic retries.");dialogueDrainFault=true;
                UpdateDialogueQueueStatus("Narration paused: "+error.Message+" Remaining events cancelled; new text stays text-only.");}
        }
        finally{if(owner==generation)activeSpeaker=previousSpeaker;dialogueDrainGate.Release();}
    }
    private async Task<DialoguePlaybackResult> SpeakObservedTextAsync(string literal,CancellationToken ct)
    {
        var observation=latestGameObservation;
        if(observation?.HasText!=true||observation.Text!=literal)return await SpeakAsync(literal,ct,VoiceFor(character.Id));
        var routes=RoutesFor(observation);
        if(observation.IsEventStream)
        {
            var accepted=AcceptDirectDialogue(observation,SpeechCheck.IsChecked==true&&!policy.Quiet&&policy.Running);
            var existing=dialogueSpeechQueue.Snapshot();
            bool matchingPending=routes.Any(route=>existing.Any(item=>item.Key==new DialogueEventKey(observation.Source,observation.SourceInstanceId!,route.Sequence)&&
                item.State is DialogueDeliveryState.Pending or DialogueDeliveryState.InFlight));
            if(accepted.Added>0||matchingPending)
            {
                // The pump may already own these exact events. Drain the existing FIFO;
                // never bypass it with a second direct playback of the same pending event.
                await DrainDialogueQueueAsync(ct);
                var states=dialogueSpeechQueue.Snapshot();
                bool completed=routes.All(route=>states.Any(item=>item.Key==new DialogueEventKey(observation.Source,observation.SourceInstanceId!,route.Sequence)&&item.State==DialogueDeliveryState.Delivered));
                return completed?DialoguePlaybackResult.Completed:DialoguePlaybackResult.Skipped;
            }
            if(accepted.Skipped>0)return DialoguePlaybackResult.Skipped;
            if(accepted.TextOnly>0)return DialoguePlaybackResult.TextOnly;
            // A deliberate Read once may replay already delivered or text-only context.
            // This is a new manual request, not an event acknowledgment or automatic retry.
            UpdateDialogueQueueStatus("Explicit manual replay of current source context; stream acknowledgment is unchanged.");
        }
        var owner=generation;var previousSpeaker=activeSpeaker;
        try
        {
            foreach(var route in routes)
            {
                ct.ThrowIfCancellationRequested();if(owner!=generation||policy.Quiet||!policy.Running)return DialoguePlaybackResult.Cancelled;
                activeSpeaker=DialogueAvatar(route.SpeakerName);
                var result=await SpeakAsync(route.Text,ct,route.VoiceId);
                if(result!=DialoguePlaybackResult.Completed){dialogueWatch.Complete(observation,result);return result;}
            }
            dialogueWatch.Complete(observation,DialoguePlaybackResult.Completed);return DialoguePlaybackResult.Completed;
        }
        finally{if(owner==generation)activeSpeaker=previousSpeaker;}
    }
    private async void TextSources_Click(object sender,RoutedEventArgs e)
    {
        if(!policy.Running||selectedWindow==null){Status("Start a selected game-window session before choosing text sources.");return;}
        var target=selectedWindow;var owner=generation;
        await RefreshVoiceChoicesAsync();if(closed||!policy.Running||selectedWindow!=target||owner!=generation)return;
        bool previousQuiet=policy.Quiet;NewTurn();policy.Quiet=previousQuiet;manualOperations++;
        try
        {
            var avatars=new[]{character}.Concat(DuoCheck.IsChecked==true?new[]{Partner}:Array.Empty<CharacterProfile>()).Select(p=>(p.Id,p.Name)).ToArray();
            var dialog=new GameTextDialog(target,gameTextOptions,dialogueSpeakerVoices,voiceChoices.Select(v=>(v.Id,v.Label)).ToArray(),preferences.DialogueRoles,
                dialogueSpeakerAvatars,avatars,dialogueSpeakerOverrides,dialogueRoleOverrides){Owner=this};
            if(dialog.ShowDialog()!=true)return;
            if(closed||!policy.Running||selectedWindow!=target){Status("Text source settings discarded because the selected game changed.");return;}
            gameText.Configure(dialog.Options);gameTextOptions=dialog.Options;
            dialogueSpeakerVoices.Clear();foreach(var mapping in dialog.Mappings)dialogueSpeakerVoices[mapping.Key]=mapping.Value;
            dialogueSpeakerAvatars.Clear();foreach(var mapping in dialog.AvatarMappings)dialogueSpeakerAvatars[mapping.Key]=mapping.Value;
            dialogueSpeakerOverrides=dialog.SpeakerOverridesEnabled;dialogueRoleOverrides=dialog.RoleOverridesEnabled;
            preferences.DialogueRoles=dialog.Roles;
            bool quiet=policy.Quiet;NewTurn();policy.Quiet=quiet;dialogueWatch.Reset();observations.Reset();
            latestGameObservation=null;latestObservationAt=null;latestFrame=null;latestText="";ObservedText.Text="";
            Status("Text sources updated. Your selected primary voice narrates by default. Optional overrides apply only for this session; saved role choices remain preserved.");
        }
        catch(Exception ex){Status("Text source settings were not applied: "+ex.Message);}
        finally{manualOperations--;await ResumeOpenMicrophoneAsync();}
    }

    // Separate modal settings surface keeps source consent and speaker mappings reviewable.
    // Nothing reads a file, clipboard body, game provider, or voice model while this dialog opens.
    private sealed class GameTextDialog:Window
    {
        private sealed record Producer(uint Id,string Name){public override string ToString()=>Name+" · PID "+Id;}
        private sealed record Mapping(string Speaker,string Voice,string Label){public override string ToString()=>Speaker+" → "+Label;}
        private sealed record AvatarChoice(string? Id,string Label);
        private readonly WindowChoice target;
        private readonly Dictionary<string,string> mappings;
        private readonly Dictionary<string,string> avatarMappings;
        private readonly (string Id,string Label)[] voices;
        private readonly (string Id,string Name)[] activeAvatars;
        private readonly CheckBox accessibility=new(){Content="Read visible accessibility text from this selected game window"};
        private readonly CheckBox fileEnabled=new(){Content="Read new lines from one subtitle / dialogue export file"};
        private readonly TextBox filePath=new(){IsReadOnly=true,MinWidth=360};
        private readonly ComboBox fileFormat=new(){ItemsSource=new[]{"Literal UTF-8 lines","Dialogue JSONL with explicit speaker metadata"},SelectedIndex=0};
        private readonly CheckBox readExisting=new(){Content="Also read existing file contents (bounded to 64 KB)",FontSize=12};
        private readonly CheckBox clipboard=new(){Content="Enable clipboard bridge from one selected running exporter"};
        private readonly ComboBox producer=new();
        private readonly ComboBox clipboardFormat=new(){ItemsSource=new[]{"Literal clipboard text","Dialogue JSONL with explicit speaker metadata"},SelectedIndex=0};
        private readonly TextBox speaker=new(){MaxLength=256};
        private readonly ComboBox voice=new(){DisplayMemberPath="Label",SelectedValuePath="Id"};
        private readonly ComboBox maleVoice=new(){DisplayMemberPath="Label",SelectedValuePath="Id"};
        private readonly ComboBox femaleVoice=new(){DisplayMemberPath="Label",SelectedValuePath="Id"};
        private readonly ComboBox neutralVoice=new(){DisplayMemberPath="Label",SelectedValuePath="Id"};
        private readonly ComboBox avatar=new(){DisplayMemberPath="Label",SelectedValuePath="Id"};
        private readonly CheckBox speakerOverrides=new(){Content="Enable exact speaker voice / active-avatar assignments for this session"};
        private readonly CheckBox roleOverrides=new(){Content="Enable male/female voices from explicit export metadata for this session"};
        private readonly ListBox mappingList=new(){Height=105};
        private readonly TextBlock notice=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Goldenrod};
        public GameTextOptions Options{get;private set;}
        public DialogueRoleVoices Roles{get;private set;}
        public IReadOnlyDictionary<string,string> Mappings=>mappings;
        public IReadOnlyDictionary<string,string> AvatarMappings=>avatarMappings;
        public bool SpeakerOverridesEnabled=>speakerOverrides.IsChecked==true;
        public bool RoleOverridesEnabled=>roleOverrides.IsChecked==true;
        public GameTextDialog(WindowChoice target,GameTextOptions options,IReadOnlyDictionary<string,string> existing,(string Id,string Label)[] voices,DialogueRoleVoices roles,
            IReadOnlyDictionary<string,string> existingAvatars,(string Id,string Name)[] activeAvatars,bool speakerEnabled,bool rolesEnabled)
        {
            this.target=target;Options=options;this.voices=voices;mappings=new(existing,StringComparer.Ordinal);Roles=roles;
            this.activeAvatars=activeAvatars;avatarMappings=new(existingAvatars,StringComparer.Ordinal);
            Title="Game text sources and speaker voices";Width=670;Height=820;MinHeight=620;WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Background=new SolidColorBrush(Color.FromRgb(16,23,34));Foreground=Brushes.White;FontSize=13;
            var panel=new StackPanel{Margin=new Thickness(20)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            Add(panel,new TextBlock{Text=target.Title+" · "+target.ProcessName+" · PID "+target.ProcessId,TextWrapping=TextWrapping.Wrap,FontSize=15});
            Add(panel,new TextBlock{Text="Connected direct sources wait for new events instead of reading menus through OCR. Source permissions and optional speaker overrides clear when the session stops.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray});
            accessibility.IsChecked=options.AccessibilityEnabled;Add(panel,accessibility);
            fileEnabled.IsChecked=options.FileProfile!=null;filePath.Text=options.FileProfile?.AllowedPath??"";
            fileFormat.SelectedIndex=options.FileProfile?.Format==GameTextFileFormat.JsonLines?1:0;readExisting.IsChecked=options.FileProfile?.ReadExisting==true;
            Add(panel,fileEnabled);var pick=new Button{Content="Choose local export file…",HorizontalAlignment=HorizontalAlignment.Left};
            pick.Click+=(_,_)=>{var picker=new OpenFileDialog{Title="Choose the game's explicit subtitle/text export",CheckFileExists=true,Multiselect=false,Filter="Text exports|*.txt;*.log;*.jsonl|All files|*.*"};if(picker.ShowDialog(this)==true){filePath.Text=picker.FileName;fileEnabled.IsChecked=true;}};
            Add(panel,pick);Add(panel,filePath);Add(panel,fileFormat);Add(panel,readExisting);
            clipboard.IsChecked=options.ClipboardProfile?.Enabled==true;clipboardFormat.SelectedIndex=options.ClipboardProfile?.Format==GameTextFileFormat.JsonLines?1:0;
            Add(panel,clipboard);Add(panel,producer);Add(panel,clipboardFormat);
            Add(panel,new TextBlock{Text="Optional external clipboard bridge: select an already running Textractor/Luna/exporter process. Only new clipboard text owned by that exact PID and process name is accepted; existing and other-app clipboard content is ignored. Verified integrated adapters for supported game profiles are managed separately. This picker does not install or inject an external hook.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray,FontSize=12});
            Add(panel,new TextBlock{Text="Optional dialogue overrides",FontWeight=FontWeights.SemiBold});
            Add(panel,new TextBlock{Text="Your selected primary companion voice is the base narrator. Unknown speakers, creatures and neutral text keep that voice. Optional exact assignments take priority over explicitly exported male/female roles. Names and prose never infer roles or activate another companion.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brushes.LightGray});
            roleOverrides.IsChecked=rolesEnabled;Add(panel,roleOverrides);
            void RolePicker(string label,ComboBox picker,string selected)
            {
                Add(panel,new TextBlock{Text=label});var items=voices.Select(v=>new VoiceChoice(v.Id,v.Label)).ToList();
                if(!items.Any(v=>v.Id==selected))items.Add(new(selected,selected+" · unavailable; choose another"));
                picker.ItemsSource=items;picker.SelectedValue=selected;Add(panel,picker);
            }
            RolePicker("Stored male choice",maleVoice,roles.Male);RolePicker("Stored female choice",femaleVoice,roles.Female);
            RolePicker("Stored neutral choice (retained; base narrator follows primary)",neutralVoice,roles.Neutral);neutralVoice.IsEnabled=false;
            void UpdateRoleControls(){maleVoice.IsEnabled=RoleOverridesEnabled;femaleVoice.IsEnabled=RoleOverridesEnabled;}
            roleOverrides.Checked+=(_,_)=>UpdateRoleControls();roleOverrides.Unchecked+=(_,_)=>UpdateRoleControls();UpdateRoleControls();
            speakerOverrides.IsChecked=speakerEnabled;Add(panel,speakerOverrides);
            Add(panel,new TextBlock{Text="Speaker name → chosen voice (explicit JSONL metadata only)",FontWeight=FontWeights.SemiBold});
            Add(panel,new TextBlock{Text="JSONL example: {\"text\":\"Literal dialogue.\",\"speaker\":\"Jinx\",\"voiceRole\":\"female\",\"sequence\":12}. Plain colon labels and lore do not select a voice.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brushes.LightGray});
            speaker.ToolTip="Exact speaker metadata spelling";Add(panel,speaker);voice.ItemsSource=voices.Select(v=>new VoiceChoice(v.Id,v.Label)).ToArray();voice.SelectedIndex=voices.Length>0?0:-1;Add(panel,voice);
            Add(panel,new TextBlock{Text="Optional avatar assignment (selected companions only)",FontSize=12});
            avatar.ItemsSource=new[]{new AvatarChoice(null,"Primary narrator portrait")}.Concat(activeAvatars.Select(p=>new AvatarChoice(p.Id,p.Name))).ToArray();avatar.SelectedIndex=0;Add(panel,avatar);
            var buttons=new WrapPanel();var assign=new Button{Content="Assign speaker voice / avatar"};assign.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(speaker.Text)||voice.SelectedValue is not string id){notice.Text="Enter an exact speaker name and select a voice.";return;}mappings[speaker.Text]=id;if(avatar.SelectedValue is string avatarId)avatarMappings[speaker.Text]=avatarId;else avatarMappings.Remove(speaker.Text);RefreshMappings();};buttons.Children.Add(assign);
            var remove=new Button{Content="Remove selected mapping"};remove.Click+=(_,_)=>{if(mappingList.SelectedItem is Mapping item){mappings.Remove(item.Speaker);avatarMappings.Remove(item.Speaker);RefreshMappings();}};buttons.Children.Add(remove);Add(panel,buttons);Add(panel,mappingList);RefreshMappings();
            Add(panel,notice);var actions=new WrapPanel();var apply=new Button{Content="Apply for this session",IsDefault=true};apply.Click+=Apply;actions.Children.Add(apply);var cancel=new Button{Content="Cancel",IsCancel=true};actions.Children.Add(cancel);Add(panel,actions);
            Loaded+=async(_,_)=>
            {
                try
                {
                var choices=await Task.Run(()=>Process.GetProcesses().Select(p=>{using(p){try{return new Producer((uint)p.Id,p.ProcessName);}catch{return null;}}}).Where(p=>p!=null).OrderBy(p=>p!.Name,StringComparer.OrdinalIgnoreCase).ToArray());
                if(!IsLoaded)return;producer.ItemsSource=choices;producer.SelectedItem=choices.FirstOrDefault(p=>p!.Id==options.ClipboardProfile?.ProducerProcessId&&p.Name==options.ClipboardProfile?.ProducerProcessName);
                }
                catch{if(IsLoaded)notice.Text="Running exporters could not be listed. File and accessibility sources remain available.";}
            };
        }
        private static void Add(Panel panel,FrameworkElement child){child.Margin=new Thickness(0,0,0,9);panel.Children.Add(child);}
        private void RefreshMappings()=>mappingList.ItemsSource=mappings.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>new Mapping(pair.Key,pair.Value,
            (voices.FirstOrDefault(v=>v.Id==pair.Value).Label??pair.Value)+(avatarMappings.TryGetValue(pair.Key,out var avatarId)?" · avatar "+(activeAvatars.FirstOrDefault(a=>a.Id==avatarId).Name??"inactive assignment ignored"):" · primary portrait"))).ToArray();
        private void Apply(object sender,RoutedEventArgs e)
        {
            if(fileEnabled.IsChecked==true&&string.IsNullOrWhiteSpace(filePath.Text)){notice.Text="Choose one local export file, or turn off the file source.";return;}
            if(clipboard.IsChecked==true&&producer.SelectedItem is not Producer){notice.Text="Choose the exact running producer process, or turn off the clipboard bridge.";return;}
            if(maleVoice.SelectedValue is not string male||femaleVoice.SelectedValue is not string female||neutralVoice.SelectedValue is not string neutral)
            {notice.Text="Choose a voice for all three dialogue roles.";return;}
            Roles=new(male,female,neutral);
            var chosen=producer.SelectedItem as Producer;
            Options=new(AccessibilityEnabled:accessibility.IsChecked==true,
                FileProfile:fileEnabled.IsChecked==true?new(filePath.Text,target.ProcessName,fileFormat.SelectedIndex==1?GameTextFileFormat.JsonLines:GameTextFileFormat.PlainLines,readExisting.IsChecked==true):null,
                ClipboardProfile:clipboard.IsChecked==true?new(target.ProcessId,chosen!.Id,chosen.Name,true,clipboardFormat.SelectedIndex==1?GameTextFileFormat.JsonLines:GameTextFileFormat.PlainLines):null);
            DialogResult=true;
        }
    }
}
