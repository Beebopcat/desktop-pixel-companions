namespace AzerothCompanion.Controls;

public enum CompanionWeaponEffect { None, Lightning, ShadowArrow, Frost, Hextech, CartoonBurst }
public sealed record CompanionWeaponDefinition(string Id,string Name,int FirstFrame,double Duration,CompanionWeaponEffect Effect);
/// <summary>Full-body atlas frame plus normalized, stage-bounded procedural FX. Frame -1 is
/// neutral completion; runtime resumes its ordinary portrait. Time is already accelerated by caller.</summary>
public sealed record CompanionWeaponFrame(int Frame,double ActorDx,double ActorDy,double ActorScale,double ActorAngle,
    CompanionWeaponEffect Effect,double EffectAlpha,double EffectPhase,double OriginX,double OriginY,
    double TargetX,double TargetY,double Radius,double Pulse,bool Done,bool Performing,string? ClipId,CompanionPerformancePhase Phase);

public sealed class CompanionWeaponAnimation
{
    private CompanionWeaponDefinition? active;
    private double started;
    public string CharacterId{get;}
    public IReadOnlyList<CompanionWeaponDefinition> Definitions{get;}
    public bool IsPerforming=>active!=null;
    public string? CurrentClipId=>active?.Id;
    public CompanionWeaponAnimation(string characterId)
    {
        CharacterId=characterId;
        var effect=characterId switch
        {"thrall"=>CompanionWeaponEffect.Lightning,"sylvanas"=>CompanionWeaponEffect.ShadowArrow,"jaina"=>CompanionWeaponEffect.Frost,
            "vi"=>CompanionWeaponEffect.Hextech,"jinx"=>CompanionWeaponEffect.CartoonBurst,_=>CompanionWeaponEffect.None};
        Definitions=effect==CompanionWeaponEffect.None?Array.Empty<CompanionWeaponDefinition>():Array.AsReadOnly(new[]{
            new CompanionWeaponDefinition("weapon.ready","Ready signature weapon",0,5.4,effect),
            new CompanionWeaponDefinition("weapon.action","Signature weapon flourish",3,6.0,effect),
            new CompanionWeaponDefinition("weapon.magic","Signature weapon effect",6,7.2,effect),
            new CompanionWeaponDefinition("weapon.rest","Lower and inspect weapon",9,5.8,effect)});
    }
    public bool Start(string id,double virtualSeconds)
    {
        ValidateTime(virtualSeconds);var clip=Definitions.FirstOrDefault(d=>d.Id==id);if(clip==null)return false;
        active=clip;started=virtualSeconds;return true;
    }
    public void Stop()=>active=null;
    public CompanionWeaponFrame Sample(double virtualSeconds)
    {
        ValidateTime(virtualSeconds);var clip=active;if(clip==null)return Idle(false,null);
        double progress=Math.Max(0,(virtualSeconds-started)/clip.Duration);
        if(progress>=1){active=null;return Idle(true,clip.Id);}
        var phase=progress<.25?CompanionPerformancePhase.Begin:progress<.78?CompanionPerformancePhase.Perform:CompanionPerformancePhase.Recover;
        int frame=clip.FirstFrame+(phase==CompanionPerformancePhase.Begin?0:phase==CompanionPerformancePhase.Perform?1:2);
        double performance=Math.Clamp((progress-.25)/.53,0,1),pulse=.5+.5*Math.Sin(performance*Math.PI*6),envelope=Envelope(progress);
        if(clip.Id=="weapon.ready")envelope*=.45;
        if(clip.Id=="weapon.rest")envelope*=.18;
        double recoil=phase==CompanionPerformancePhase.Perform?Math.Sin(performance*Math.PI*4):0;
        double dx=0,dy=0,angle=0,scale=.91,ox=.65,oy=.47,tx=.83,ty=.45,radius=.12;
        switch(clip.Effect)
        {
            case CompanionWeaponEffect.Lightning:
                ox=.67;oy=.44;tx=.76;ty=.68;dx=-.025*envelope;dy=-.025*envelope*pulse;angle=-3*envelope;radius=.11+.035*pulse;break;
            case CompanionWeaponEffect.ShadowArrow:
                ox=.61;oy=.46;tx=.87;ty=.37;dx=-.04*envelope;angle=-4*envelope;radius=.075+.025*pulse;break;
            case CompanionWeaponEffect.Frost:
                ox=.65;oy=.41;tx=.69;ty=.60;dx=-.018*envelope;dy=-.025*envelope;angle=2*envelope;radius=.13+.035*pulse;break;
            case CompanionWeaponEffect.Hextech:
                ox=.64;oy=.56;tx=.79;ty=.52;dx=-.025*envelope+.009*recoil;dy=.01*envelope;angle=-5*envelope*recoil;radius=.10+.025*pulse;break;
            case CompanionWeaponEffect.CartoonBurst:
                ox=.67;oy=.49;tx=.87;ty=.43;dx=-.035*envelope+.012*recoil;dy=-.006*recoil;angle=-3*envelope*recoil;radius=.075+.022*pulse;break;
        }
        // Weapon action lives in the authored pose and FX, never whole-body resizing.
        dx=dy=angle=0;scale=1;
        // Entire effect footprint stays on the avatar stage. Projectiles never cross into the game.
        ox=Math.Clamp(ox,radius+.02,1-radius-.02);oy=Math.Clamp(oy,radius+.02,1-radius-.02);
        tx=Math.Clamp(tx,.08,.92);ty=Math.Clamp(ty,.08,.92);
        return new(frame,dx,dy,scale,angle,clip.Effect,envelope,performance,ox,oy,tx,ty,radius,pulse,false,true,clip.Id,phase);
    }
    private static double Envelope(double progress)
    {
        if(progress<=.20||progress>=.92)return 0;
        if(progress<.34)return Smooth((progress-.20)/.14);
        if(progress>.76)return 1-Smooth((progress-.76)/.16);
        return 1;
    }
    private static double Smooth(double value){value=Math.Clamp(value,0,1);return value*value*(3-2*value);}
    private static CompanionWeaponFrame Idle(bool done,string? id)=>new(-1,0,0,1,0,CompanionWeaponEffect.None,0,0,.5,.5,.5,.5,0,0,done,false,id,CompanionPerformancePhase.Idle);
    private static void ValidateTime(double time){if(!double.IsFinite(time))throw new ArgumentOutOfRangeException(nameof(time));}
}
