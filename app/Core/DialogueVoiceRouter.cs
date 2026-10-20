using AzerothCompanion.Services;

namespace AzerothCompanion.Core;

public sealed record DialogueRoleVoices(
    string Male="qn_private_matthew_mcconaughey",string Female="qn_private_sylvanas",string Neutral="qn_lorath_refined")
{
    public string For(DialogueVoiceRole role)=>role switch{DialogueVoiceRole.Male=>Male,DialogueVoiceRole.Female=>Female,_=>Neutral};
}
public sealed record DialogueVoiceRoute(string Text,string? SpeakerName,long Sequence,string VoiceId,bool ExplicitSpeakerMapping,
    DialogueVoiceRole Role=DialogueVoiceRole.Neutral,bool ExplicitRoleMetadata=false);
public static class DialogueVoiceRouter
{
    public static IReadOnlyList<DialogueVoiceRoute> Route(GameTextObservation observation,
        IReadOnlyDictionary<string,string> speakerVoiceMappings,string narratorVoiceId,DialogueRoleVoices? roles=null,
        bool enableSpeakerOverrides=false,bool enableRoleOverrides=false)
    {
        ArgumentNullException.ThrowIfNull(observation);ArgumentNullException.ThrowIfNull(speakerVoiceMappings);
        if(string.IsNullOrWhiteSpace(narratorVoiceId))throw new ArgumentException("Choose a narrator voice explicitly.",nameof(narratorVoiceId));
        if(!observation.HasText)return Array.Empty<DialogueVoiceRoute>();
        if(enableRoleOverrides&&(roles==null||new[]{roles.Male,roles.Female}.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Choose male and female voices before enabling explicit role overrides.",nameof(roles));
        return observation.Lines.Select(line=>
        {
            bool structured=observation.Source is GameTextSource.DialogueFile or GameTextSource.ClipboardBridge or GameTextSource.VerifiedHook;
            bool mapped=enableSpeakerOverrides&&structured&&line.SpeakerName!=null&&speakerVoiceMappings.TryGetValue(line.SpeakerName,out var voice)&&!string.IsNullOrWhiteSpace(voice);
            // Only explicit structured export metadata chooses a gendered voice.
            // Names, OCR prose and research snippets never infer speaker gender.
            bool explicitRole=structured;
            var role=explicitRole?line.VoiceRole:DialogueVoiceRole.Neutral;
            return new DialogueVoiceRoute(line.Text,line.SpeakerName,line.Sequence,
                mapped?speakerVoiceMappings[line.SpeakerName!]:enableRoleOverrides&&role!=DialogueVoiceRole.Neutral?roles?.For(role)??narratorVoiceId:narratorVoiceId,mapped,role,explicitRole&&role!=DialogueVoiceRole.Neutral);
        }).ToArray();
    }

    public static string AvatarForSpeaker(string? speaker,IReadOnlyDictionary<string,string> explicitAssignments,
        string primaryId,string? enabledPartnerId,bool enabled)
    {
        if(enabled&&speaker!=null&&explicitAssignments.TryGetValue(speaker,out var assigned)&&
            (assigned==primaryId||enabledPartnerId!=null&&assigned==enabledPartnerId))return assigned;
        return primaryId;
    }
}
