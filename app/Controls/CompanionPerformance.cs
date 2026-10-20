namespace AzerothCompanion.Controls;

public enum CompanionPerformancePhase { Idle, Begin, Perform, Recover }
public sealed record CompanionPropDefinition(int Index,string Id,string Name,string Category,string AssetKey,
    int Columns,int Rows,int CellIndex,int Frames=1);
public sealed record CompanionPerformanceKeyframe(double At,string Bank,int Frame,
    double ActorDx=0,double ActorDy=0,double ActorScale=1,double ActorAngle=0,
    double PropDx=.65,double PropDy=.64,double PropWidth=.24,double PropAlpha=0,double PropAngle=0,int PropFrame=0);
public sealed record CompanionAnimationDefinition(string Id,string Name,string Category,double Duration,
    int PropIndex,IReadOnlyList<CompanionPerformanceKeyframe> Keyframes,bool RequiresPartner=false);
public sealed record CompanionAnimationAvailability(string Id,bool Available,string? Reason);
/// <summary>Actor offsets are translations from stage center. PropDx/Dy are absolute normalized
/// centers; PropWidth is a fraction of min(stage width,height). PropFrame offsets the prop's CellIndex.
/// Banks refer to authored 12-cell pose sheets. These are choreographed sequences, not 70 drawings.</summary>
public sealed record CompanionPerformanceFrame(string Bank,int Frame,double ActorDx,double ActorDy,double ActorScale,double ActorAngle,
    int PropIndex,int PropFrame,double PropDx,double PropDy,double PropWidth,double PropAlpha,double PropAngle,
    bool Done,bool Performing,string? ClipId,CompanionPerformancePhase Phase)
{
    public bool HasProp=>PropIndex>=0&&PropAlpha>0;
    public double Progress{get;init;}
    public double RealElapsedSeconds{get;init;}
}

/// <summary>Pure per-character catalog and deterministic sampler. Caller supplies virtual time,
/// including any desired 3× acceleration. No timer, renderer, file access, model, or audio lives here.</summary>
public sealed class CompanionPerformance
{
    private readonly Dictionary<string,CompanionAnimationDefinition> clips;
    private readonly Random random;
    private readonly Queue<int> propBag=new();
    private CompanionAnimationDefinition? active;
    private double started;
    private int lastProp=-1;
    public string CharacterId{get;}
    public string ThemedPropSource{get;}
    public IReadOnlyList<CompanionPropDefinition> Props{get;}
    public IReadOnlyList<CompanionAnimationDefinition> Definitions{get;}
    public bool IsPerforming=>active!=null;
    public string? CurrentClipId=>active?.Id;
    public int CurrentPropIndex=>active?.PropIndex??-1;
    public CompanionPerformance(string characterId,string? themedPropSource=null,int seed=0)
    {
        if(string.IsNullOrWhiteSpace(characterId))throw new ArgumentException("Character ID is required.",nameof(characterId));
        CharacterId=characterId;
        // A character may only use its own themed objects. The newer cast gets
        // distinct neutral objects rather than weapons borrowed from another cast member.
        ThemedPropSource=characterId is "sylvanas" or "jaina" or "rowan"?characterId:"neutral";
        if(themedPropSource!=null&&themedPropSource!=ThemedPropSource)
            throw new ArgumentException("Cross-character prop borrowing is disabled.",nameof(themedPropSource));
        random=new Random(unchecked(seed^StableHash(characterId)));Props=Array.AsReadOnly(BuildProps(ThemedPropSource));
        var definitions=new List<CompanionAnimationDefinition>();AddGestures(definitions);AddThemedProps(definitions);AddFurniture(definitions);AddPets(definitions);AddDuo(definitions);
        CompanionSylvanasActions.ReplaceDefinitions(characterId,definitions);
        if(Props.Count!=30||definitions.Count!=70)throw new InvalidOperationException("The per-character catalog must contain 30 props and 70 clips.");
        Definitions=definitions.AsReadOnly();clips=definitions.ToDictionary(clip=>clip.Id,StringComparer.Ordinal);
        foreach(var definition in definitions)Validate(definition);
    }
    public bool Start(string id,double virtualSeconds)
    {ValidateTime(virtualSeconds);if(string.IsNullOrWhiteSpace(id))return false;id=id switch{"wave"=>"gesture.wave","duo-wave"=>"duo.wave","high-five"=>"duo.high-five",_=>id};if(!clips.TryGetValue(id,out var clip))return false;active=clip;started=virtualSeconds;return true;}
    public bool PlayAnimation(string id,double virtualSeconds)=>Start(id,virtualSeconds);
    public void Stop()=>active=null;
    public string GetDefaultClipId(int propIndex)
    {
        if(propIndex<0||propIndex>=Props.Count)throw new ArgumentOutOfRangeException(nameof(propIndex));
        var prop=Props[propIndex];return prop.Category switch
        {"themed"=>"prop."+prop.Id,"furniture"=>"furniture."+prop.Id,_=>"pet."+prop.Id+".play"};
    }
    public int NextPropIndex()
    {
        if(propBag.Count==0)
        {
            var items=Enumerable.Range(0,Props.Count).ToArray();for(int i=items.Length-1;i>0;i--){int j=random.Next(i+1);(items[i],items[j])=(items[j],items[i]);}
            if(items[0]==lastProp)(items[0],items[1])=(items[1],items[0]);foreach(var item in items)propBag.Enqueue(item);
        }
        return lastProp=propBag.Dequeue();
    }
    public int SummonRandomProp(double virtualSeconds)
    {ValidateTime(virtualSeconds);int index=NextPropIndex();Start(GetDefaultClipId(index),virtualSeconds);return index;}
    public CompanionPerformanceFrame Sample(double virtualSeconds)
    {
        ValidateTime(virtualSeconds);var clip=active;if(clip==null)return Idle(false,null);
        double progress=Math.Max(0,(virtualSeconds-started)/clip.Duration);
        if(progress>=1){active=null;return Idle(true,clip.Id);}
        var frames=clip.Keyframes;int index=0;while(index+1<frames.Count&&frames[index+1].At<=progress)index++;
        var left=frames[index];var right=frames[Math.Min(index+1,frames.Count-1)];
        double portion=right.At==left.At?0:(progress-left.At)/(right.At-left.At);portion=Math.Clamp(portion,0,1);portion=portion*portion*(3-2*portion);
        double L(double a,double b)=>a+(b-a)*portion;
        // Authored poses supply movement. Never resize or rotate a whole body to fake contact.
        const double angle=0,scale=1;
        double propAngle=Math.Clamp(L(left.PropAngle,right.PropAngle),-180,180),propExtent=RotationExtent(propAngle);
        double width=Math.Clamp(L(left.PropWidth,right.PropWidth),.08,Math.Min(.72,.96/propExtent)),propHalf=width*propExtent/2;
        int propFrame=clip.PropIndex<0?0:Math.Clamp(left.PropFrame,0,Props[clip.PropIndex].Frames-1);
        double realSeconds=progress*clip.Duration/3;
        int actorFrame=left.Bank=="pc"?CompanionPcMotion.Sample(realSeconds).ActorFrame:left.Frame;
        if(clip.PropIndex>=0&&Props[clip.PropIndex].Category is "pet" or "fantasy-pet"&&left.Bank=="activity"&&left.Frame is 6 or 7)
        {
            // Existing authored invitation/reaching poses follow the animal's leap;
            // a long hold no longer leaves the person frozen through every jump.
            bool play=clip.Id.EndsWith(".play",StringComparison.Ordinal);
            bool greeting=clip.Id.EndsWith(".greet",StringComparison.Ordinal);
            bool reaction=(greeting||play)&&(realSeconds is >=5.95 and <7.5 || play&&realSeconds is >=9.95 and <11.5);
            actorFrame=reaction?7:left.Frame;
        }
        return new(left.Bank,actorFrame,0,0,scale,angle,
            clip.PropIndex,propFrame,Math.Clamp(L(left.PropDx,right.PropDx),propHalf+.02,1-propHalf-.02),Math.Clamp(L(left.PropDy,right.PropDy),propHalf+.02,1-propHalf-.02),width,Math.Clamp(L(left.PropAlpha,right.PropAlpha),0,1),propAngle,
            false,true,clip.Id,progress<.24?CompanionPerformancePhase.Begin:progress<.78?CompanionPerformancePhase.Perform:CompanionPerformancePhase.Recover){Progress=progress,RealElapsedSeconds=realSeconds};
    }
    private static double RotationExtent(double angle){double radians=angle*Math.PI/180;return Math.Abs(Math.Cos(radians))+Math.Abs(Math.Sin(radians));}
    private static CompanionPerformanceFrame Idle(bool done,string? id)=>new("basic",0,0,0,1,0,-1,0,.5,.5,.24,0,0,done,false,id,CompanionPerformancePhase.Idle);
    private static void ValidateTime(double time){if(!double.IsFinite(time))throw new ArgumentOutOfRangeException(nameof(time));}
    private static int StableHash(string text){uint hash=2166136261;foreach(char c in text)hash=unchecked((hash^c)*16777619);return unchecked((int)hash);}
    private static string Slug(string name)=>name.Replace(' ','-');
    // Painted props for the neutral set (Assets/neutral-props-v1.png, 5 x 1); the rest stay procedural.
    private static int? NeutralArtCell(string id)=>id switch{"book"=>0,"map-case"=>1,"arcade-cabinet"=>2,"camera"=>3,"treasure-chest"=>4,_=>null};
    private static CompanionPropDefinition[] BuildProps(string source)
    {
        string[] names=source switch
        {
            "sylvanas"=>["bow","quiver-of-arrows","raven","dark-lantern","dagger","silver-hand-mirror","scroll","compass","purple-potion","black-rose","music-box","purple-spellbook","chess-queen","silver-goblet","soul-crystal","small-banner","spectral-butterfly"],
            "jaina"=>["frost-staff","arcane-orb","spellbook","teacup","snowflake","water-elemental","map","quill","blue-potion","crystal-ball","telescope","hourglass","seashell","lyre","paper-bird","rune-stone","ice-boat"],
            "rowan"=>["guitar","coffee-cup","compass","trail-map","lantern","telescope","fishing-rod","backpack","sketchbook","juggling-balls","hat","kite","camp-stool","sandwich","harmonica","chess-knight","paper-airplane"],
            _=>["book","mug","compass","folded-map","lantern","sketchbook","paper-plane","flower","arcade-cabinet","hourglass","chess-piece","sandwich","treasure-chest","camera","kite","map-case","scroll"]
        };
        var props=names.Select((name,i)=>source=="neutral"&&NeutralArtCell(name) is int cell?
            new CompanionPropDefinition(i,name,name.Replace('-',' '),"themed","neutral-props-v1",5,1,cell):
            new CompanionPropDefinition(i,name,name.Replace('-',' '),"themed",source=="neutral"?"procedural-neutral-v2":source+"-props-v1",source=="neutral"?1:6,source=="neutral"?1:3,i)).ToList();
        string[] furniture=["table-chair","couch-tv","popcorn","pc","handheld","bed","soccer"];
        for(int i=0;i<furniture.Length;i++)props.Add(new(props.Count,furniture[i],furniture[i].Replace('-',' '),"furniture","procedural-furniture-v2",1,1,i));
        (string Id,string Bank,int Row)[] ordinary=source switch
        {
            "sylvanas"=>[("black-cat","cats",1),("husky","dogs",2),("tabby-cat","cats",0)],
            "jaina"=>[("white-cat","cats",2),("corgi","dogs",1),("golden-puppy","dogs",0)],
            _=>[("tabby-cat","cats",0),("golden-puppy","dogs",0),("husky","dogs",2)]
        };
        foreach(var pet in ordinary)props.Add(new(props.Count,pet.Id,pet.Id.Replace('-',' '),"pet","shared-"+pet.Bank+"-v1",4,3,pet.Row*4,4));
        string[] fantasy=["baby-dragon","griffin","spirit-fox"];
        for(int i=0;i<3;i++)props.Add(new(props.Count,fantasy[i],fantasy[i].Replace('-',' '),"fantasy-pet","shared-fantasy-v1",4,3,i*4,4));
        return props.ToArray();
    }
    private static CompanionPerformanceKeyframe K(double at,string bank,int frame,double x=0,double y=0,double scale=1,double angle=0,
        double px=.65,double py=.64,double width=.24,double alpha=0,double propAngle=0,int propFrame=0)=>new(at,bank,frame,0,0,1,0,px,py,width,alpha,propAngle,propFrame);
    private static void Add(List<CompanionAnimationDefinition> list,string id,string name,string category,double duration,int prop,CompanionPerformanceKeyframe[] keys,bool duo=false)=>
        list.Add(new(id,name,category,duration,prop,Array.AsReadOnly(keys),duo));
    private static void AddGestures(List<CompanionAnimationDefinition> list)
    {
        Add(list,"gesture.breathe","Quiet breathing","gesture",4,-1,[K(0,"basic",0),K(.22,"basic",2,scale:.97,y:-.012),K(.55,"basic",0,scale:.985),K(.83,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.blink","Curious blink","gesture",3,-1,[K(0,"basic",0),K(.16,"basic",1),K(.25,"basic",0,angle:3,scale:.94),K(.55,"basic",1,angle:-2,scale:.96),K(.83,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.glance-left","Look left","gesture",3.5,-1,[K(0,"basic",0),K(.2,"basic",2,x:-.035,scale:.9),K(.48,"basic",6,x:-.055,angle:-5,scale:.86),K(.74,"basic",9,x:-.02,scale:.92),K(1,"basic",0)]);
        Add(list,"gesture.glance-right","Look right","gesture",3.7,-1,[K(0,"basic",0),K(.18,"basic",2,x:.03,scale:.92),K(.42,"basic",6,x:.06,angle:6,scale:.84),K(.68,"basic",8,x:.025,scale:.94),K(1,"basic",0)]);
        Add(list,"gesture.wave","Friendly wave","gesture",4.5,-1,[K(0,"basic",0),K(.18,"basic",11,scale:.92,angle:-4),K(.37,"basic",11,angle:6,scale:.87),K(.55,"basic",11,angle:-6,scale:.87),K(.8,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.nod","Agreeing nod","gesture",3.6,-1,[K(0,"basic",0),K(.2,"basic",9,y:.025,scale:.9,angle:4),K(.36,"basic",0,y:-.01,scale:.96),K(.54,"basic",9,y:.03,scale:.88,angle:5),K(.8,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.shake-head","Gentle disagreement","gesture",4.1,-1,[K(0,"basic",0),K(.2,"basic",2,x:-.03,scale:.9,angle:-7),K(.38,"basic",2,x:.03,scale:.9,angle:7),K(.56,"basic",2,x:-.025,scale:.9,angle:-5),K(.8,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.think","Consider an idea","gesture",6,-1,[K(0,"basic",0),K(.2,"basic",8,scale:.92),K(.45,"basic",8,x:-.018,y:.012,scale:.91,angle:-3),K(.66,"basic",8,x:.012,scale:.94,angle:2),K(.83,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.listen","Attentive listening","gesture",5.5,-1,[K(0,"basic",0),K(.18,"basic",9,scale:.94,angle:3),K(.43,"basic",9,x:.025,scale:.9,angle:5),K(.7,"basic",1,scale:.95),K(.86,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.laugh","Warm laugh","gesture",4.8,-1,[K(0,"basic",0),K(.2,"basic",10,scale:.92),K(.35,"basic",10,y:-.035,scale:.87,angle:-4),K(.48,"basic",10,y:.025,scale:.87,angle:4),K(.63,"basic",10,y:-.025,scale:.9),K(.83,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.bow","Courteous bow","gesture",4.4,-1,[K(0,"basic",0),K(.18,"basic",6,scale:.91,angle:6),K(.45,"basic",6,y:.07,scale:.78,angle:18),K(.64,"basic",6,y:.03,scale:.86,angle:9),K(.84,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.stretch","Standing stretch","gesture",5.1,-1,[K(0,"basic",0),K(.2,"activity",9,scale:.9,y:-.015),K(.46,"activity",9,y:-.04,scale:.88,angle:-4),K(.65,"activity",9,y:-.018,scale:.92,angle:4),K(.83,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.yawn","Sleepy yawn","gesture",5.8,-1,[K(0,"basic",0),K(.17,"basic",1,scale:.94),K(.4,"activity",9,y:.025,scale:.88,angle:5),K(.65,"basic",1,scale:.92,y:.01),K(.84,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.shrug","Playful shrug","gesture",3.9,-1,[K(0,"basic",0),K(.2,"motion",10,x:-.015,scale:.93,angle:-3),K(.42,"motion",11,y:-.025,scale:.9),K(.66,"basic",2,angle:4,scale:.91),K(.82,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.cheer","Small celebration","gesture",4.6,-1,[K(0,"basic",0),K(.19,"basic",11,scale:.89),K(.33,"basic",10,y:-.06,scale:.82,angle:-5),K(.48,"basic",11,y:.02,scale:.9),K(.63,"basic",10,y:-.045,scale:.84,angle:5),K(.82,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.lean-in","Lean closer","gesture",4.3,-1,[K(0,"basic",0),K(.22,"basic",6,x:.02,scale:.93),K(.46,"basic",6,x:.055,y:-.02,scale:.87,angle:7),K(.66,"basic",9,x:.02,scale:.93),K(.84,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.salute","Companion salute","gesture",4.2,-1,[K(0,"basic",0),K(.16,"motion",8,x:-.01,scale:.95),K(.38,"basic",11,angle:-5,scale:.88),K(.65,"basic",11,y:-.015,scale:.9),K(.82,"basic",7),K(1,"basic",0)]);
        Add(list,"gesture.tiptoe","Two careful steps","gesture",5,-1,[K(0,"basic",0),K(.10,"motion",0),K(.19,"motion",1),K(.28,"motion",2),K(.37,"motion",3),K(.46,"motion",4),K(.55,"motion",5),K(.64,"motion",6),K(.73,"motion",7),K(.85,"basic",7),K(1,"basic",0)]);
    }
    private void AddThemedProps(List<CompanionAnimationDefinition> list)
    {
        // Seventeen authored spatial/action plans. Atlas identity supplies the character's object;
        // each plan has its own reach, handling trajectory, pose progression and recovery.
        (double Width,double X,double Y,double Ax,double Ay,double Angle,int Pose,string Bank)[] plans=
        [(.40,.62,.53,-.06,.02,-12,11,"motion"),(.22,.64,.44,-.08,.04,4,10,"motion"),(.28,.76,.59,-.10,.07,-7,7,"activity"),
         (.32,.68,.42,-.075,.035,9,11,"motion"),(.27,.66,.61,-.07,.01,-16,10,"motion"),(.29,.64,.40,-.06,.03,5,8,"basic"),
         (.39,.56,.63,-.035,.04,-4,8,"basic"),(.23,.61,.59,-.055,.02,11,9,"basic"),(.24,.60,.40,-.075,.035,6,11,"motion"),
         (.31,.70,.64,-.08,.065,-8,6,"activity"),(.27,.64,.64,-.065,.025,-3,9,"basic"),(.41,.57,.62,-.035,.04,3,8,"basic"),
         (.29,.68,.78,-.09,.09,-6,10,"motion"),(.23,.60,.42,-.065,.025,8,11,"motion"),(.30,.71,.46,-.09,.05,-11,8,"basic"),
         (.34,.70,.49,-.085,.03,14,11,"basic"),(.26,.75,.49,-.10,.025,-4,11,"motion")];
        if(ThemedPropSource=="jaina")
        {
            plans[2]=(.40,.57,.63,-.045,.04,3,8,"basic"); // Read the spellbook.
            plans[4]=(.28,.70,.44,-.07,.035,-12,11,"motion"); // Levitate a snowflake.
            plans[5]=(.29,.75,.72,-.085,.08,4,7,"activity"); // Invite the miniature elemental.
            plans[7]=(.25,.60,.63,-.05,.03,11,11,"motion"); // Write with the quill.
            plans[9]=(.32,.68,.48,-.075,.04,-5,8,"basic"); // Focus on the crystal ball.
            plans[10]=(.36,.65,.40,-.065,.025,-7,11,"motion"); // Look through the telescope.
            plans[11]=(.26,.61,.53,-.04,.035,12,8,"basic"); // Turn the hourglass.
            plans[12]=(.25,.63,.40,-.06,.025,-4,9,"basic"); // Listen to the seashell.
            plans[13]=(.37,.59,.58,-.065,.03,5,11,"motion"); // Pluck the lyre.
            plans[14]=(.29,.73,.48,-.075,.03,-9,11,"motion"); // Release the paper bird.
            plans[15]=(.28,.69,.52,-.065,.025,4,11,"motion"); // Hold a rune stone.
            plans[16]=(.32,.70,.73,-.075,.06,-3,11,"motion"); // Float the miniature boat.
        }
        else if(ThemedPropSource=="rowan")
        {
            plans[2]=(.24,.62,.58,-.055,.025,-5,9,"basic"); // Check the compass.
            plans[3]=(.39,.57,.62,-.04,.035,5,8,"basic"); // Study the trail map.
            plans[4]=(.32,.68,.39,-.075,.03,-9,11,"motion"); // Lift the lantern.
            plans[5]=(.35,.64,.40,-.06,.025,-6,11,"motion"); // Aim the telescope.
            plans[6]=(.46,.69,.57,-.075,.04,-18,11,"motion"); // Cast the fishing rod.
            plans[7]=(.32,.66,.63,-.065,.035,7,10,"motion"); // Adjust the backpack.
            plans[8]=(.39,.55,.63,-.025,.04,4,11,"motion"); // Draw in the sketchbook.
            plans[9]=(.28,.66,.57,-.065,.025,-7,11,"motion"); // Juggle above the hands.
            plans[10]=(.30,.61,.34,-.035,.025,-5,10,"motion"); // Tip the hat.
            plans[11]=(.35,.75,.41,-.08,.02,8,11,"motion"); // Fly a kite.
            plans[14]=(.29,.61,.43,-.055,.025,-5,11,"motion"); // Play the harmonica.
            plans[15]=(.27,.67,.77,-.07,.08,7,10,"motion"); // Place the chess knight.
        }
        for(int i=0;i<17;i++)
        {
            var p=plans[i];double shift=.018+i*.0013;int action=i%3;
            var keys=new List<CompanionPerformanceKeyframe>{K(0,"basic",0),K(.08,"motion",8,scale:.92),K(.18,"motion",9,scale:.9),
                K(.245,"motion",10,x:p.Ax,y:p.Ay,scale:.77,px:p.X-.025,py:p.Y+.07,width:p.Width,alpha:1,propAngle:-p.Angle/2),
                K(.34,p.Bank,p.Pose,x:p.Ax,y:p.Ay,scale:.77,angle:p.Angle/3,px:p.X,py:p.Y,width:p.Width,alpha:1,propAngle:p.Angle),
                K(.48,action==0?"motion":p.Bank,action==0?11:p.Pose,x:p.Ax-shift,y:p.Ay-.012,scale:.76,angle:-p.Angle/4,px:p.X+shift,py:p.Y-.045,width:p.Width,alpha:1,propAngle:-p.Angle),
                K(.63,p.Bank,p.Pose,x:p.Ax+shift/2,y:p.Ay+.012,scale:.78,angle:p.Angle/5,px:p.X-shift,py:p.Y+.025,width:p.Width,alpha:1,propAngle:p.Angle*.65),
                K(.77,"motion",11,x:p.Ax,y:p.Ay,scale:.8,px:p.X,py:p.Y,width:p.Width,alpha:1),
                K(.87,"motion",10,x:p.Ax/2,scale:.9,px:p.X,py:p.Y+.05,width:p.Width*.85,alpha:0),K(.94,"basic",7),K(1,"basic",0)};
            // Object-specific handling: fly, twirl, read, drink, and juggle do not share paths.
            if(i==16||ThemedPropSource=="jaina"&&i==14||ThemedPropSource=="rowan"&&i==11)
            {keys[5]=keys[5] with{PropDx=.78,PropDy=.29,PropAngle=-25};keys[6]=keys[6] with{PropDx=.83,PropDy=.38,PropAngle=22};}
            if(i==4&&ThemedPropSource=="sylvanas"||i==9&&ThemedPropSource=="rowan")
            {keys[5]=keys[5] with{PropDy=.36,PropAngle=85};keys[6]=keys[6] with{PropDy=.50,PropAngle=-80};}
            if(ThemedPropSource=="rowan"&&i==0){keys[5]=keys[5] with{ActorAngle=0,PropAngle=-9};keys[6]=keys[6] with{ActorAngle=0,PropAngle=9};}
            if(ThemedPropSource=="rowan"&&i==6){keys[5]=keys[5] with{PropDx=.73,PropDy=.34,PropAngle=-30};keys[6]=keys[6] with{PropDx=.74,PropDy=.59,PropAngle=25};}
            if(ThemedPropSource=="jaina"&&i==16){keys[5]=keys[5] with{PropDx=.75,PropDy=.74,PropAngle=0};keys[6]=keys[6] with{PropDx=.80,PropDy=.72,PropAngle=3};}
            if(ThemedPropSource=="rowan"&&i==12){keys[4]=keys[4] with{Bank="activity",Frame=4,ActorScale=1,ActorDy=.12,PropDy=.78};keys[5]=keys[4] with{At=.48,Frame=5};keys[6]=keys[4] with{At=.63,Frame=4};}
            Add(list,"prop."+Props[i].Id,"Use "+Props[i].Name,"themed",5.3+i*.17,i,keys.ToArray());
        }
    }
    private static List<CompanionPerformanceKeyframe> Summon()=>[K(0,"basic",0),K(.08,"motion",8,scale:.92),K(.18,"motion",9,scale:.9)];
    private static void Recover(List<CompanionPerformanceKeyframe> keys,double x,double y,double width)
    {keys.Add(K(.87,"motion",10,scale:.88,px:x,py:y,width:width,alpha:0));keys.Add(K(.94,"basic",7));keys.Add(K(1,"basic",0));}
    private void AddFurniture(List<CompanionAnimationDefinition> list)
    {
        for(int index=17;index<24;index++)
        {
            int kind=index-17;var keys=Summon();double px=.58,py=.70,width=.58,duration=9;
            switch(kind)
            {
                case 0: // Take a seat at the table, then work and stand up.
                    keys.AddRange([K(.25,"activity",0,x:-.055,y:.09,scale:.67,px:.56,py:.70,width:.61,alpha:1),K(.37,"activity",1,x:-.06,y:.095,scale:.67,px:.56,py:.70,width:.61,alpha:1),K(.49,"activity",0,x:-.045,y:.09,scale:.68,px:.56,py:.70,width:.61,alpha:1),K(.62,"activity",1,x:-.055,y:.1,scale:.66,px:.56,py:.70,width:.61,alpha:1),K(.77,"activity",9,x:-.035,y:.03,scale:.80,px:.56,py:.70,width:.61,alpha:1)]);duration=9.2;break;
                case 1: // Couch and television with controller poses.
                    px=.58;py=.68;width=.70;keys.AddRange([K(.25,"activity",2,x:-.045,y:.09,scale:.63,px:px,py:py,width:width,alpha:1),K(.4,"activity",2,x:-.055,y:.10,scale:.64,angle:-3,px:px,py:py,width:width,alpha:1),K(.55,"activity",2,x:-.03,y:.08,scale:.65,angle:3,px:px,py:py,width:width,alpha:1),K(.68,"activity",2,x:-.04,y:.095,scale:.63,px:px,py:py,width:width,alpha:1),K(.77,"basic",10,scale:.72,px:px,py:py,width:width,alpha:1)]);duration=54;break;
                case 2: // Hold/reach with authored hand poses; an eating pose is not supplied.
                    px=.63;py=.62;width=.31;keys.AddRange([K(.25,"motion",10,x:-.045,scale:.84,px:px,py:py,width:width,alpha:1),K(.4,"activity",4,x:-.035,scale:.84,px:.59,py:.46,width:width,alpha:1,propAngle:8),K(.54,"activity",5,x:-.045,y:-.025,scale:.83,px:px,py:py,width:width,alpha:1),K(.66,"motion",10,x:-.03,scale:.87,px:px,py:py,width:width,alpha:1),K(.77,"activity",4,scale:.89,px:px,py:py,width:width,alpha:1)]);duration=7.8;break;
                case 3: // Rear-view playing holds for fourteen real seconds, then stands.
                    px=.61;py=.70;width=.55;
                    keys.AddRange([K(.205,"motion",10,px:px,py:py,width:width,alpha:1),K(.24,"pc",0,px:px,py:py,width:width,alpha:1),K(.50,"pc",0,px:px,py:py,width:width,alpha:1),K(.70,"pc",0,px:px,py:py,width:width,alpha:1),K(.86,"activity",9,px:px,py:py,width:width,alpha:1),K(.925,"motion",10,px:px,py:py,width:width,alpha:0),K(.972,"basic",7),K(1,"basic",0)]);duration=66;break;
                case 4: // Handheld gaming stays near the hands instead of a desk.
                    px=.54;py=.63;width=.25;keys.AddRange([K(.25,"activity",4,y:.07,scale:.75,px:px,py:py,width:width,alpha:1),K(.4,"activity",5,x:-.022,y:.075,scale:.75,angle:-4,px:px-.02,py:py-.02,width:width,alpha:1,propAngle:-8),K(.54,"activity",4,x:.025,y:.06,scale:.76,angle:4,px:px+.02,py:py,width:width,alpha:1,propAngle:8),K(.68,"activity",5,y:.07,scale:.76,px:px,py:py,width:width,alpha:1),K(.77,"activity",4,y:.015,scale:.85,px:px,py:py,width:width,alpha:1)]);duration=8.6;break;
                case 5: // Authored horizontal sleeping pose over the bed, then stretch.
                    px=.54;py=.66;width=.68;keys.AddRange([K(5.0/24,"activity",8,px:px,py:py,width:width,alpha:1),K(.4,"activity",8,px:px,py:py,width:width,alpha:1),K(.57,"activity",8,px:px,py:py,width:width,alpha:1),K(.75,"activity",8,px:px,py:py,width:width,alpha:1),K(20.5/24,"activity",9,px:px,py:py,width:width,alpha:1),K(21.7/24,"motion",10,px:px,py:py,width:width,alpha:0),K(23.0/24,"basic",7),K(1,"basic",0)]);duration=72;break;
                default: // Soccer: plant, kick, follow the flight and recover.
                    px=.74;py=.81;width=.19;keys.AddRange([K(.25,"activity",10,x:-.10,y:.08,scale:.75,px:.67,py:.82,width:width,alpha:1),K(.39,"activity",11,x:-.07,y:.085,scale:.75,angle:-4,px:.72,py:.73,width:width,alpha:1,propAngle:35),K(.50,"activity",11,x:-.04,y:.02,scale:.76,px:.84,py:.42,width:width,alpha:1,propAngle:110),K(.63,"basic",9,x:-.08,scale:.8,px:.79,py:.70,width:width,alpha:1,propAngle:-110),K(.77,"activity",10,x:-.09,y:.08,scale:.76,px:px,py:py,width:width,alpha:1,propAngle:15)]);duration=7.2;break;
            }
            if(kind is not (3 or 5))Recover(keys,px,py,width);
            Add(list,"furniture."+Props[index].Id,"Use "+Props[index].Name,"furniture",duration,index,keys.ToArray());
        }
    }
    private void AddPets(List<CompanionAnimationDefinition> list)
    {
        string[] actions=["greet","play","settle","feed"];
        for(int index=24;index<30;index++)for(int action=0;action<4;action++)
        {
            var keys=Summon();int pet=index-24;double px=.73+pet*.012,py=.80-pet*.006,width=.22+pet*.007,ax=-.105+pet*.004;
            keys.Add(K(.25,"activity",7,x:ax,y:.08,scale:.72,px:px+.04,py:py,width:width,alpha:1,propFrame:0));
            switch(action)
            {
                case 0: // Invite, approach, acknowledge the animal's greeting.
                    keys.AddRange([K(.34,"activity",6,x:ax,y:.095,scale:.72,px:px-.02,py:py,width:width,alpha:1,propFrame:1),K(.54,"activity",6,x:ax,y:.02,scale:.77,px:px-.04,py:py-.07,width:width,alpha:1,propFrame:2),K(.68,"activity",6,x:ax,y:.10,scale:.72,px:px-.045,py:py,width:width,alpha:1,propFrame:0),K(.77,"activity",7,x:ax,y:.06,scale:.75,px:px,py:py,width:width,alpha:1,propFrame:0)]);break;
                case 1: // Animal crouches, leaps and lands while companion follows.
                    keys.AddRange([K(.37,"activity",6,x:ax,y:.11,scale:.70,px:px-.035,py:py+.01,width:width,alpha:1,propFrame:1),K(.50,"activity",6,x:ax-.018,y:.06,scale:.72,angle:-3,px:px+.025,py:py-.19,width:width,alpha:1,propAngle:-8,propFrame:2),K(.61,"activity",6,x:ax+.015,y:.02,scale:.76,px:px-.055,py:py-.09,width:width,alpha:1,propAngle:6,propFrame:2),K(.70,"activity",6,x:ax,y:.10,scale:.71,px:px-.06,py:py,width:width,alpha:1,propFrame:1),K(.78,"activity",7,x:ax,y:.06,scale:.74,px:px-.025,py:py,width:width,alpha:1,propFrame:0)]);break;
                case 2: // Settle into an authored curled sleeping pose.
                    keys.AddRange([K(.39,"activity",6,x:ax,y:.11,scale:.70,px:px-.05,py:py,width:width,alpha:1,propFrame:1),K(.52,"activity",6,x:ax,y:.105,scale:.71,px:px-.055,py:py,width:width,alpha:1,propFrame:3),K(.66,"activity",6,x:ax,y:.035,scale:.76,px:px-.055,py:py+.005,width:width*.98,alpha:1,propFrame:3),K(.78,"activity",6,x:ax,y:.025,scale:.77,px:px-.055,py:py,width:width,alpha:1,propFrame:3)]);break;
                default: // Kneel and offer food by hand; the animal lowers its head.
                    keys.AddRange([K(.36,"activity",6,x:ax-.015,y:.12,scale:.69,px:px-.07,py:py,width:width,alpha:1,propFrame:0),K(.47,"activity",6,x:ax+.015,y:.08,scale:.70,px:px-.09,py:py+.01,width:width,alpha:1,propFrame:1),K(.59,"activity",6,x:ax-.01,y:.12,scale:.69,px:px-.09,py:py,width:width,alpha:1,propFrame:1),K(.70,"activity",6,x:ax,y:.085,scale:.73,px:px-.055,py:py-.06,width:width,alpha:1,propFrame:2),K(.79,"activity",6,x:ax,y:.105,scale:.71,px:px-.04,py:py,width:width,alpha:1,propFrame:0)]);break;
            }
            Recover(keys,px,py,width);Add(list,"pet."+Props[index].Id+"."+actions[action],actions[action]+" "+Props[index].Name,"pet",3*(18+pet*.30+action*.85),index,keys.ToArray());
        }
    }
    private void AddDuo(List<CompanionAnimationDefinition> list)
    {
        Add(list,"duo.wave","Wave to another companion","duo",5.4,-1,[K(0,"basic",0),K(.17,"basic",11,x:.06,scale:.83,angle:5),K(.35,"basic",11,x:.08,y:-.02,scale:.80,angle:-5),K(.52,"basic",11,x:.06,scale:.83,angle:6),K(.70,"basic",9,x:.03,scale:.91),K(.85,"basic",7),K(1,"basic",0)],true);
        Add(list,"duo.high-five","Companion high five","duo",5.9,-1,[K(0,"basic",0),K(.15,"motion",2,x:.06,scale:.86),K(.28,"motion",6,x:.10,scale:.77),K(.43,"motion",10,x:.11,y:-.02,scale:.75,angle:6),K(.56,"basic",11,x:.105,y:-.025,scale:.75,angle:-4),K(.70,"basic",10,x:.055,scale:.87),K(.86,"basic",7),K(1,"basic",0)],true);
        Add(list,"duo.show-prop","Show a summoned object","duo",6.8,0,[K(0,"basic",0),K(.08,"motion",8,scale:.92),K(.18,"motion",9,scale:.90),K(.25,"motion",10,x:-.055,scale:.79,px:.63,py:.59,width:.31,alpha:1),K(.42,"motion",11,x:.04,scale:.78,px:.70,py:.50,width:.34,alpha:1,propAngle:-12),K(.61,"basic",11,x:.065,scale:.77,px:.74,py:.53,width:.34,alpha:1,propAngle:10),K(.77,"motion",11,x:.025,scale:.86,px:.65,py:.61,width:.31,alpha:1),K(.88,"motion",10,scale:.90,alpha:0),K(.94,"basic",7),K(1,"basic",0)],true);
        Add(list,"duo.soccer","Pass a soccer ball","duo",8.1,23,[K(0,"basic",0),K(.08,"motion",8,scale:.92),K(.18,"motion",9,scale:.90),K(.25,"activity",10,x:-.095,y:.085,scale:.74,px:.68,py:.82,width:.19,alpha:1),K(.37,"activity",11,x:-.035,y:.06,scale:.76,px:.76,py:.70,width:.19,alpha:1,propAngle:55),K(.49,"basic",9,x:.015,scale:.79,px:.85,py:.57,width:.19,alpha:1,propAngle:125),K(.63,"activity",10,x:-.035,y:.075,scale:.76,px:.74,py:.76,width:.19,alpha:1,propAngle:-55),K(.77,"basic",11,x:.03,scale:.85,px:.66,py:.82,width:.19,alpha:1),K(.88,"motion",10,scale:.9,alpha:0),K(.94,"basic",7),K(1,"basic",0)],true);
    }
    private static void Validate(CompanionAnimationDefinition definition)
    {
        if(definition.Duration<=0||!double.IsFinite(definition.Duration)||definition.Keyframes.Count<4||definition.Keyframes[0].At!=0||definition.Keyframes[^1].At!=1)throw new InvalidOperationException("A clip requires a full begin, performance and recovery.");
        double previous=-1;bool snapped=false;
        foreach(var key in definition.Keyframes)
        {
            if(key.At<=previous||key.At>1||key.Bank is not ("basic" or "motion" or "activity" or "pc" or CompanionSylvanasActions.StepsBank or CompanionSylvanasActions.HandlingBank)||key.Frame<0||key.Frame>11)throw new InvalidOperationException("Invalid pose timeline.");
            if(key.Bank=="motion"&&key.Frame==9)snapped=true;
            if(definition.PropIndex>=0&&key.PropAlpha>0&&!snapped)throw new InvalidOperationException("Props must appear after the finger snap.");
            previous=key.At;
        }
        if(definition.Keyframes[^1].Bank!="basic"||definition.Keyframes[^1].Frame!=0||definition.Keyframes[^1].PropAlpha!=0)throw new InvalidOperationException("A clip must recover to neutral without a prop.");
    }
}
