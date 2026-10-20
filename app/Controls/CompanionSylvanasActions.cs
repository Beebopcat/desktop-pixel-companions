namespace AzerothCompanion.Controls;

/// <summary>Three explicitly authored repairs. Equipment belongs to the source pose,
/// so the generic hand/prop overlay is never used for these actions.</summary>
public static class CompanionSylvanasActions
{
    public const string StepsBank="sylvanas-steps",HandlingBank="sylvanas-handling";
    public static readonly string[] ClipIds=["gesture.tiptoe","prop.bow","prop.quiver-of-arrows"];
    public static string? RequiredBank(string character,string id)=>character!="sylvanas"?null:id switch
    {"gesture.tiptoe"=>StepsBank,"prop.bow" or "prop.quiver-of-arrows"=>HandlingBank,_=>null};
    internal static void ReplaceDefinitions(string character,List<CompanionAnimationDefinition> definitions)
    {
        if(character!="sylvanas")return;
        foreach(string id in ClipIds)
        {
            int index=definitions.FindIndex(d=>d.Id==id);if(index<0)throw new InvalidOperationException("Missing repair target "+id);
            var original=definitions[index];
            CompanionPerformanceKeyframe K(double at,string bank,int frame)=>new(at,bank,frame);
            var neutral=K(1,"basic",0);
            // One complete ordered left/right cycle is two footfalls, followed by a
            // feet-together recovery. No omitted passing/down poses or body jiggle.
            CompanionPerformanceKeyframe[] keys=id switch
            {
                "gesture.tiptoe"=>[K(0,"basic",0),K(.06,StepsBank,8),K(.14,StepsBank,0),K(.23,StepsBank,1),K(.32,StepsBank,2),K(.41,StepsBank,3),K(.50,StepsBank,4),K(.59,StepsBank,5),K(.68,StepsBank,6),K(.77,StepsBank,7),K(.86,StepsBank,9),K(.95,"basic",0),neutral],
                "prop.bow"=>[K(0,"basic",0),K(.07,HandlingBank,0),K(.19,HandlingBank,1),K(.32,HandlingBank,2),K(.46,HandlingBank,3),K(.64,HandlingBank,4),K(.79,HandlingBank,5),K(.93,"basic",0),neutral],
                _=>[K(0,"basic",0),K(.07,HandlingBank,6),K(.20,HandlingBank,7),K(.33,HandlingBank,8),K(.46,HandlingBank,9),K(.59,HandlingBank,10),K(.73,HandlingBank,11),K(.88,HandlingBank,6),K(.96,"basic",0),neutral]
            };
            definitions[index]=original with{Duration=id=="gesture.tiptoe"?10.8:18,Keyframes=Array.AsReadOnly(keys)};
        }
        int couch=definitions.FindIndex(d=>d.Id=="furniture.couch-tv");
        if(couch<0)throw new InvalidOperationException("Missing couch repair target");
        // Give the watch phase time to play the authored TV pictures. Body poses
        // and the caller's constant three-times clock retain their original rules.
        definitions[couch]=definitions[couch] with{Duration=54};
    }
}
