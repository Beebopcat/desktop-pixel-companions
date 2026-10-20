namespace AzerothCompanion.Controls;

public readonly record struct InteractionPoint(double X,double Y)
{
    public static InteractionPoint operator +(InteractionPoint a,InteractionPoint b)=>new(a.X+b.X,a.Y+b.Y);
    public static InteractionPoint operator -(InteractionPoint a,InteractionPoint b)=>new(a.X-b.X,a.Y-b.Y);
    public static InteractionPoint operator *(InteractionPoint a,double b)=>new(a.X*b,a.Y*b);
    public double Distance(InteractionPoint other)=>Math.Sqrt(Math.Pow(X-other.X,2)+Math.Pow(Y-other.Y,2));
}
public readonly record struct InteractionRect(double X,double Y,double Width,double Height)
{
    public InteractionPoint Map(InteractionPoint point)=>new(X+point.X*Width,Y+point.Y*Height);
    public double Bottom=>Y+Height;
}
/// <summary>Anchors are fractions of the immutable 256-pixel actor canvas, not its changing alpha bounds.</summary>
public sealed record InteractionRig(InteractionPoint Feet,InteractionPoint Seat,InteractionPoint Head,
    InteractionPoint NearHand,InteractionPoint FarHand,bool Verified)
{
    public InteractionPoint? KickFoot{get;init;}
    public InteractionRect? VisibleBounds{get;init;}
}
public enum CompanionContactKind { None,Held,Seated,Sleep,Pet,Kick }
/// <summary>One layered composition. ActorCanvas.Width is always the caller's constant body size.
/// Targets and contact evidence are separate from art approval: inferred anchors never become verified.</summary>
public sealed record CompanionInteractionScene(InteractionRect ActorCanvas,InteractionRect PropCanvas,
    InteractionPoint Seat,InteractionPoint TableHand,InteractionPoint Head,InteractionPoint NearHand,
    InteractionPoint FarHand,InteractionPoint ContactTarget,CompanionContactKind ContactKind,
    double ContactError,bool ContactActive,bool ContactsVerified,bool MirrorPet,bool HeadOnLeft,
    string? PropId,string? ClipId,double Alpha,int PropFrame)
{
    public bool ContactRequested{get;init;}
    public double RealElapsedSeconds{get;init;}
    public InteractionPoint HandContactTarget{get;init;}
    public double HandContactError{get;init;}
    public bool AuthoredCouch{get;init;}
}

/// <summary>Pure contact placement. It translates a fixed-size canvas; there is no body rotation,
/// body scaling, process access, timer, drawing, or audio. Reference anchors keep furniture still.</summary>
public static class CompanionInteractionLayout
{
    public static CompanionInteractionScene Compose(CompanionPerformanceFrame frame,CompanionPropDefinition? prop,
        InteractionRig rig,InteractionRig reference,double actorSide=1,InteractionPoint? petHead=null,InteractionPoint? referencePetHead=null,int? renderedPetFrame=null,CompanionPetMotionFrame? petMotion=null)
    {
        if(!double.IsFinite(actorSide)||actorSide is <=0 or >1)throw new ArgumentOutOfRangeException(nameof(actorSide));
        var origin=new InteractionPoint(.5-actorSide*rig.Feet.X,1-actorSide*rig.Feet.Y);
        var seatTarget=new InteractionPoint(.35,1-actorSide*(reference.Feet.Y-reference.Seat.Y));
        var refOrigin=seatTarget-reference.Seat*actorSide;
        var handTarget=refOrigin+reference.NearHand*actorSide;
        var kind=CompanionContactKind.None;var propRect=new InteractionRect(.6,.7,.2,.2);
        // The bed direction belongs to its fixed sleeping reference. Deriving it
        // from a standing/waking pose would flip the furniture during the same clip.
        bool contact=false,headLeft=(prop?.Id=="bed"?reference.Head.X<reference.Seat.X:rig.Head.X<rig.Seat.X),mirrorPet=false;
        InteractionPoint target=new(.5,.5);
        if(prop!=null&&frame.PropAlpha>0)
        {
            if(prop.Category is "pet" or "fantasy-pet")
            {
                kind=CompanionContactKind.Pet;mirrorPet=false;
                // Pets keep a common row scale and grounded feet. Their authored head pose
                // and the actor's real reaching hand determine contact, never arbitrary spin.
                var hp=petHead??new InteractionPoint(.25,.35);
                const double petSide=.28; // Same animal canvas across every character and pose.
                var groundedHand=origin+rig.NearHand*actorSide;
                var referenceHand=new InteractionPoint(.5-actorSide*reference.Feet.X,1-actorSide*reference.Feet.Y)+reference.NearHand*actorSide;
                var motion=petMotion??CompanionPetMotion.Sample(frame,1-actorSide*(reference.Feet.Y-reference.NearHand.Y));
                contact=motion.ContactRequested;
                double petY=1-petSide-motion.JumpHeight;
                if(contact&&motion.JumpHeight==0)
                {
                    // The neutral/crouched head is a contact AREA, not one magic pixel.
                    // Keep paws on the floor and choose within its estimated crown/face area.
                    hp=hp with{Y=Math.Clamp((groundedHand.Y-petY)/petSide,hp.Y-.13,hp.Y+.13)};
                }
                // A raised reaction hand never drags the animal through the actor.
                // Keep its approach anchored to the fixed ground-level reach.
                double outward=motion.JumpHeight>0?.08*Math.Sin(Math.PI*Math.Clamp(motion.JumpHeight/.14,0,1)/2):0;
                double x=Math.Clamp(referenceHand.X-petSide*hp.X+motion.OffsetX+outward,.001,1-petSide-.001);
                propRect=new(x,petY,petSide,petSide);target=propRect.Map(hp);
                // The actor remains grounded. If available stage space prevents exact
                // contact, report the residual; never move knees off the floor to hide it.
            }
            else if(prop.Id is "table-chair" or "pc" or "couch-tv")
            {
                kind=CompanionContactKind.Seated;
                bool pc=prop.Id=="pc";
                // Chair height follows the character's measured seated leg length.
                // Preserve ground contact instead of suspending every different
                // body at one arbitrary pelvis height. The complete authored desk
                // and chair follow this fixed reference for the whole scene.
                if(pc){seatTarget=new(.36,1-actorSide*(reference.Feet.Y-reference.Seat.Y));handTarget=seatTarget+(reference.NearHand-reference.Seat)*actorSide;}
                contact=pc?frame.Bank=="pc":frame.Bank=="activity"&&frame.Frame is >=0 and <=3;
                if(contact)
                {
                    origin=seatTarget-rig.Seat*actorSide;
                    // Estimated pelvis anchors can differ by a fraction of a pixel.
                    // Correct a ground overrun as a translation of the complete scene,
                    // keeping seat contact, tabletop and constant-sized body together.
                    double overrun=Math.Max(0,origin.Y+rig.Feet.Y*actorSide-1);
                    if(overrun>0){origin=origin-new InteractionPoint(0,overrun);seatTarget=seatTarget-new InteractionPoint(0,overrun);handTarget=handTarget-new InteractionPoint(0,overrun);}
                }
                propRect=pc?new(0,0,1,1):new(.14,Math.Min(seatTarget.Y,handTarget.Y)-.04,.78,1-Math.Min(seatTarget.Y,handTarget.Y)+.04);
                target=seatTarget;
            }
            else if(prop.Id=="bed")
            {
                kind=CompanionContactKind.Sleep;contact=frame.Bank=="activity"&&frame.Frame==8;
                // A horizontal mattress fits the horizontal authored sleeping pose. Keep
                // one mattress location while the person settles and wakes up.
                target=new(headLeft?.175:.825,.65); // the authored pillow position
                if(contact)origin=target-rig.Head*actorSide;
                propRect=new(0,0,1,1);
            }
            else if(prop.Id=="soccer")
            {
                kind=CompanionContactKind.Kick;
                // The authored kick is raised: the ball bounces to that boot before
                // release. The former ground-only ball was never struck and then
                // teleported back whenever the body changed to a recovery pose.
                origin=KickOrigin(rig,actorSide);
                var kickOrigin=KickOrigin(reference,actorSide);
                var impact=kickOrigin+(reference.KickFoot??reference.Feet)*actorSide;
                // A wide authored kick needs a smaller ball in the remaining
                // stage space; clipping or shrinking the actor would hide a defect.
                double ball=Math.Clamp(1-.012-impact.X,.04,.12);
                double hit=frame.ClipId=="duo.soccer"?.37:.39,p=frame.Progress;
                double ballX=Math.Clamp(impact.X,.015,1-ball-.012),ballY=1-ball-.004;
                if(p<hit)
                {
                    double rise=Math.Clamp((p-.25)/(hit-.25),0,1);rise=rise*rise*(3-2*rise);
                    ballY+=(impact.Y-ball/2-ballY)*rise;
                }
                else if(p<hit+.04)
                {ballY=impact.Y-ball/2;contact=frame.Bank=="activity"&&frame.Frame==11;}
                else
                {
                    double flight=Math.Clamp((p-hit-.04)/.30,0,1);
                    ballX+=(1-ball-.012-ballX)*flight;
                    ballY=impact.Y-ball/2+(1-ball-.004-(impact.Y-ball/2))*flight-Math.Sin(flight*Math.PI)*.14;
                }
                propRect=new(ballX,ballY,ball,ball);
                target=propRect.Map(new(0,.5));
            }
            else
            {
                kind=CompanionContactKind.Held;contact=true;
                var hand=origin+rig.NearHand*actorSide;
                double size=prop.Id switch{"book" or "folded-map" or "scroll" or "sketchbook" or "arcade-cabinet" or "treasure-chest"=>.19,"umbrella" or "kite"=>.24,"camera" or "map-case"=>.16,_=>.14};
                // Atlas objects and procedural objects share a measured grip. Objects do
                // not float along a synthetic path disconnected from the authored hand.
                var grip=Grip(prop.Id);
                if(prop.Id=="popcorn")size=.20;
                if(prop.Id=="popcorn"&&frame.Bank=="activity"&&frame.Frame is 4 or 5)
                {
                    var far=origin+rig.FarHand*actorSide;
                    hand=new((hand.X+far.X)/2,(hand.Y+far.Y)/2);
                }
                if(prop.Id=="handheld")
                {
                    var far=origin+rig.FarHand*actorSide;
                    size=Math.Clamp(Math.Abs(hand.X-far.X)+.07,.16,.24);
                    hand=new((hand.X+far.X)/2,(hand.Y+far.Y)/2);grip=new(.5,.65);
                }
                propRect=new(hand.X-size*grip.X,hand.Y-size*grip.Y,size,size);
                target=propRect.Map(grip);
            }
        }
        var actor=new InteractionRect(origin.X,origin.Y,actorSide,actorSide);
        var seat=actor.Map(rig.Seat);var handPoint=actor.Map(rig.NearHand);
        double error=contact?kind switch{CompanionContactKind.Seated=>seat.Distance(target),CompanionContactKind.Sleep=>actor.Map(rig.Head).Distance(target),CompanionContactKind.Kick=>actor.Map(rig.KickFoot??rig.Feet).Distance(target),_=>handPoint.Distance(target)}:0;
        bool requested=contact;
        if(kind==CompanionContactKind.Pet)contact=contact&&error<=1.0/47;
        return new(actor,propRect,seatTarget,handTarget,actor.Map(rig.Head),handPoint,actor.Map(rig.FarHand),target,kind,error,contact,
            rig.Verified&&reference.Verified,mirrorPet,headLeft,prop?.Id,frame.ClipId,frame.PropAlpha,renderedPetFrame??frame.PropFrame){ContactRequested=requested,RealElapsedSeconds=frame.RealElapsedSeconds,HandContactTarget=handTarget,HandContactError=kind==CompanionContactKind.Seated&&contact?handPoint.Distance(handTarget):0};
    }
    public static InteractionPoint Grip(string id)=>id switch
    {"lantern" or "dark-lantern"=>new(.5,.09),"umbrella" or "kite"=>new(.5,.87),"flower" or "black-rose"=>new(.5,.8),"popcorn"=>new(.5,.18),_=>new(.5,.5)};
    private static InteractionPoint KickOrigin(InteractionRig rig,double side)
    {
        double x=.44-side*rig.Feet.X;
        if(rig.VisibleBounds is{ } bounds)
            x=Math.Clamp(x,1.0/47-side*bounds.X,1-1.0/47-side*(bounds.X+bounds.Width));
        return new(x,1-side*rig.Feet.Y);
    }
}
