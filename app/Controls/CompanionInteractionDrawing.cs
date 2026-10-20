using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;

/// <summary>Runtime layered drawing, shared by review fixtures. No raster files are modified.
/// Furniture has explicit back/actor/foreground ordering; neutral objects are original vector art.</summary>
internal static class CompanionInteractionDrawing
{
    private static readonly Dictionary<string,Brush> Palette=new(StringComparer.Ordinal);
    private static readonly Brush Wood=B("#A2683A"),Edge=B("#50321F"),Light=B("#D29A54"),Cloth=B("#327B8D"),
        DarkCloth=B("#205062"),Metal=B("#97B1B9"),Ink=B("#233442"),Paper=B("#E6D7B5"),Gold=B("#E7BD59"),Glow=B("#64D9E9"),Rose=B("#E2839E");
    internal static void Draw(DrawingContext dc,CompanionInteractionScene scene,BitmapSource actor,BitmapSource? prop,
        double width,double height,bool facingLeft)
    {
        if(width<=0||height<=0)return;
        double side=Math.Min(width,height),left=(width-side)/2,top=height-side;
        dc.PushClip(new RectangleGeometry(new Rect(0,0,width,height)));
        dc.PushTransform(new TranslateTransform(left,top));dc.PushTransform(new ScaleTransform(side,side));
        if(facingLeft)dc.PushTransform(new MatrixTransform(-1,0,0,1,1,0));
        if(scene.PropId=="pc"&&CompanionSceneArt.HasPc)
        {
            CompanionPcDrawing.DrawScene(dc,scene,actor);
            if(facingLeft)dc.Pop();
            dc.Pop();dc.Pop();dc.Pop();
            return;
        }
        if(scene.Alpha>0&&scene.ContactKind is CompanionContactKind.Seated or CompanionContactKind.Sleep)
        {dc.PushOpacity(scene.Alpha);DrawFurnitureBack(dc,scene);dc.Pop();}
        if(scene.ContactKind==CompanionContactKind.Sleep&&scene.ContactActive&&CompanionSceneArt.HasBed)
        {
            CompanionBedDrawing.DrawExposedActor(dc,scene,actor);
        }
        else dc.DrawImage(actor,R(scene.ActorCanvas));
        if(scene.Alpha>0)
        {
            dc.PushOpacity(scene.Alpha);
            if(scene.ContactKind is CompanionContactKind.Seated or CompanionContactKind.Sleep)
            {DrawFurnitureFront(dc,scene);if(scene.ContactKind==CompanionContactKind.Seated&&scene.ContactActive){DrawHandOverProp(dc,scene,actor);DrawHandOverProp(dc,scene,actor,scene.FarHand);}}
            else if(scene.ContactKind==CompanionContactKind.Pet&&prop!=null)
            {
                if(scene.MirrorPet)
                {var r=scene.PropCanvas;dc.PushTransform(new MatrixTransform(-1,0,0,1,2*r.X+r.Width,0));dc.DrawImage(prop,R(r));dc.Pop();}
                else dc.DrawImage(prop,R(scene.PropCanvas));
                // Redraw a small SOURCE hand region over the pet, rather than an invented
                // arm or a full-body scale/rotation. This establishes contact layering.
                if(scene.ContactActive)DrawHandOverProp(dc,scene,actor);
            }
            else if(scene.ContactKind==CompanionContactKind.Kick)DrawBall(dc,scene.PropCanvas,scene.RealElapsedSeconds);
            else if(prop!=null)dc.DrawImage(prop,R(scene.PropCanvas));
            else DrawNeutral(dc,scene.PropId??"book",scene.PropCanvas,scene.RealElapsedSeconds);
            if(scene.ContactKind==CompanionContactKind.Held&&scene.ContactActive)
            {
                DrawHandOverProp(dc,scene,actor,radiusFactor:.022);
                if(scene.PropId is "handheld" or "popcorn")DrawHandOverProp(dc,scene,actor,scene.FarHand,.022);
            }
            dc.Pop();
        }
        if(facingLeft)dc.Pop();dc.Pop();dc.Pop();dc.Pop();
    }
    private static Rect R(InteractionRect r)=>new(r.X,r.Y,Math.Max(.001,r.Width),Math.Max(.001,r.Height));
    private static void Rect(DrawingContext dc,Brush fill,double x,double y,double w,double h,Brush? stroke=null,double line=.008)=>dc.DrawRectangle(fill,stroke==null?null:new Pen(stroke,line),new(x,y,Math.Max(.001,w),Math.Max(.001,h)));
    private static void Line(DrawingContext dc,Brush color,double x1,double y1,double x2,double y2,double thickness=.008)=>dc.DrawLine(new Pen(color,thickness),new(x1,y1),new(x2,y2));
    private static void DrawFurnitureBack(DrawingContext dc,CompanionInteractionScene s)
    {
        if(s.AuthoredCouch){CompanionCouchArt.DrawBack(dc,s);return;}
        if(s.PropId=="pc")
        {
            if(CompanionSceneArt.HasPc)
            {
                CompanionPcDrawing.DrawBack(dc,s);
            }
            return;
        }
        if(s.ContactKind==CompanionContactKind.Sleep)
        {
            if(CompanionSceneArt.HasBed)DrawBedLayer(dc,s,CompanionSceneArt.Bed[0],1);
            return;
        }
        double sx=s.Seat.X,sy=s.Seat.Y;
        if(s.PropId=="couch-tv")
        {
            dc.DrawRoundedRectangle(DarkCloth,new Pen(Ink,.012),new(sx-.20,sy-.19,.46,.25),.035,.035);
            Rect(dc,Cloth,sx-.22,sy-.045,.49,.09,Ink);Rect(dc,Wood,sx-.19,.97,.05,.03);Rect(dc,Wood,sx+.20,.97,.05,.03);
            Rect(dc,Ink,.77,.57,.19,.23,Metal);Rect(dc,Glow,.79,.59,.15,.16);Line(dc,Ink,.86,.80,.86,.98,.018);
        }
        else
        {
            Rect(dc,Wood,sx-.13,sy-.21,.22,.23,Edge);Rect(dc,Cloth,sx-.12,sy-.195,.20,.16,Edge,.004);
            Rect(dc,Wood,sx-.14,sy-.012,.27,.035,Edge);Line(dc,Edge,sx-.11,sy+.02,sx-.11,1,.025);Line(dc,Edge,sx+.10,sy+.02,sx+.10,1,.025);
        }
    }
    private static void DrawFurnitureFront(DrawingContext dc,CompanionInteractionScene s)
    {
        if(s.AuthoredCouch){CompanionCouchArt.DrawFront(dc,s);return;}
        if(s.PropId=="pc")
        {
            if(CompanionSceneArt.HasPc)CompanionPcDrawing.DrawFront(dc,s);
            return;
        }
        if(s.ContactKind==CompanionContactKind.Sleep)
        {
            if(CompanionSceneArt.HasBed)
            {
                CompanionBedDrawing.DrawBlanket(dc,s);
            }
            return;
        }
        if(s.PropId=="couch-tv")
        {
            Rect(dc,Cloth,s.Seat.X-.22,s.Seat.Y+.035,.06,.12,Ink);Rect(dc,Cloth,s.Seat.X+.21,s.Seat.Y+.035,.06,.12,Ink);
            return;
        }
        double y=s.TableHand.Y+.012,x=Math.Clamp(s.TableHand.X-.16,.20,.52),right=Math.Min(.94,x+.43);
        // Table front hides the upper thighs but has OPEN space beneath, so dangling
        // feet remain visible under the top. Hands are redrawn above this foreground.
        Rect(dc,Wood,x,y,right-x,.042,Edge);Rect(dc,Light,x-.012,y-.014,right-x+.024,.022,Edge,.005);
        Line(dc,Edge,x+.023,y+.038,x+.023,1,.024);Line(dc,Edge,right-.022,y+.038,right-.022,1,.024);
    }
    private static void DrawBedLayer(DrawingContext dc,CompanionInteractionScene scene,BitmapSource source,double opacity)
    {
        if(opacity<=0)return;dc.PushOpacity(opacity);
        if(!scene.HeadOnLeft)dc.PushTransform(new MatrixTransform(-1,0,0,1,1,0));
        dc.DrawImage(source,new Rect(0,0,1,1));
        if(!scene.HeadOnLeft)dc.Pop();dc.Pop();
    }
    private static void DrawHandOverProp(DrawingContext dc,CompanionInteractionScene s,BitmapSource actor,InteractionPoint? anchor=null,double radiusFactor=.055)
    {
        double radius=s.ActorCanvas.Width*radiusFactor;
        var hand=anchor??s.NearHand;
        dc.PushClip(new EllipseGeometry(new Point(hand.X,hand.Y),radius,radius));
        dc.DrawImage(actor,R(s.ActorCanvas));dc.Pop();
    }
    private static void DrawBall(DrawingContext dc,InteractionRect r,double seconds)
    {
        var c=r.Map(new(.5,.5));double radius=r.Width*.5;
        dc.DrawEllipse(B("#F3EFE0"),new Pen(Ink,.006),new(c.X,c.Y),radius,radius);
        dc.PushClip(new EllipseGeometry(new(c.X,c.Y),radius,radius));
        dc.PushTransform(new RotateTransform(seconds*130,c.X,c.Y));
        var center=Enumerable.Range(0,5).Select(i=>new Point(c.X+Math.Cos(-Math.PI/2+i*Math.PI*2/5)*radius*.33,c.Y+Math.Sin(-Math.PI/2+i*Math.PI*2/5)*radius*.33)).ToArray();
        Polygon(dc,Ink,null,center);
        for(int i=0;i<5;i++)
        {
            double a=-Math.PI/2+i*Math.PI*2/5;
            double x=c.X+Math.Cos(a)*radius*.90,y=c.Y+Math.Sin(a)*radius*.90;
            var patch=Enumerable.Range(0,5).Select(n=>new Point(x+Math.Cos(a+n*Math.PI*2/5)*radius*.23,y+Math.Sin(a+n*Math.PI*2/5)*radius*.23)).ToArray();
            Polygon(dc,Ink,null,patch);Line(dc,Ink,center[i].X,center[i].Y,x,y,.003);
        }
        dc.Pop();dc.Pop();
    }
    private static void DrawNeutral(DrawingContext dc,string id,InteractionRect r,double seconds)
    {
        dc.PushTransform(new TranslateTransform(r.X,r.Y));dc.PushTransform(new ScaleTransform(r.Width,r.Height));
        var pen=new Pen(Ink,.045);
        switch(id)
        {
            case "mug" or "coffee-cup" or "teacup":dc.DrawEllipse(null,new Pen(Metal,.09),new(.82,.47),.15,.20);Rect(dc,Paper,.18,.17,.57,.66,Ink,.04);Line(dc,Metal,.27,.28,.65,.28,.035);break;
            case "compass":dc.DrawEllipse(Gold,pen,new(.5,.5),.43,.43);dc.DrawEllipse(Paper,null,new(.5,.5),.34,.34);Line(dc,Rose,.3,.73,.7,.27,.08);Line(dc,Ink,.5,.17,.5,.83,.022);break;
            case "folded-map":Polygon(dc,Paper,pen,new(.05,.1),new(.33,.19),new(.65,.05),new(.95,.14),new(.95,.88),new(.65,.79),new(.33,.93),new(.05,.83));Line(dc,Wood,.33,.19,.33,.93,.04);Line(dc,Wood,.65,.05,.65,.79,.04);Line(dc,Cloth,.15,.65,.82,.35,.04);break;
            case "lantern":Line(dc,Gold,.33,.12,.67,.12,.06);Line(dc,Gold,.33,.12,.28,.32,.06);Line(dc,Gold,.67,.12,.72,.32,.06);Rect(dc,Gold,.2,.3,.6,.6,Ink,.04);Rect(dc,Glow,.29,.39,.42,.39);Line(dc,Ink,.5,.31,.5,.89,.04);break;
            case "flower" or "black-rose":Line(dc,Cloth,.5,.35,.5,.96,.09);for(int i=0;i<5;i++){double a=i*Math.PI*2/5;dc.DrawEllipse(Rose,pen,new(.5+Math.Cos(a)*.18,.28+Math.Sin(a)*.18),.15,.15);}dc.DrawEllipse(Gold,null,new(.5,.28),.12,.12);break;
            case "paper-plane":Polygon(dc,Paper,pen,new(.03,.08),new(.95,.44),new(.42,.91),new(.40,.57));Line(dc,Metal,.03,.08,.40,.57,.035);break;
            case "puzzle-cube":Rect(dc,Cloth,.12,.12,.76,.76,Ink,.05);for(int i=1;i<3;i++){Line(dc,Gold,.12+i*.25,.12,.12+i*.25,.88,.055);Line(dc,Gold,.12,.12+i*.25,.88,.12+i*.25,.055);}break;
            case "hourglass":Rect(dc,Wood,.17,.03,.66,.1,Ink,.04);Rect(dc,Wood,.17,.87,.66,.1,Ink,.04);Polygon(dc,Metal,pen,new(.24,.13),new(.76,.13),new(.56,.5),new(.76,.87),new(.24,.87),new(.44,.5));Polygon(dc,Gold,null,new(.30,.77),new(.7,.77),new(.5,.54));break;
            case "chess-piece":Rect(dc,Paper,.18,.76,.64,.13,Ink,.04);Rect(dc,Paper,.38,.30,.24,.47,Ink,.04);dc.DrawEllipse(Paper,pen,new(.5,.24),.16,.16);break;
            case "sandwich":Polygon(dc,Paper,pen,new(.08,.85),new(.5,.10),new(.92,.85));Line(dc,Cloth,.19,.69,.81,.69,.07);Line(dc,Rose,.15,.79,.85,.79,.055);break;
            case "coin":dc.DrawEllipse(Gold,pen,new(.5,.5),.38,.44);dc.DrawEllipse(null,new Pen(Light,.06),new(.5,.5),.25,.32);Line(dc,Wood,.5,.3,.5,.7,.07);break;
            case "umbrella":dc.DrawEllipse(Cloth,pen,new(.5,.36),.44,.29);Rect(dc,Brushes.Transparent,0,.4,1,.2);Line(dc,Gold,.5,.35,.5,.95,.06);break;
            case "kite":Polygon(dc,Rose,pen,new(.5,.02),new(.90,.36),new(.5,.83),new(.10,.36));Line(dc,Paper,.5,.02,.5,.83,.04);Line(dc,Gold,.5,.83,.75,.98,.04);break;
            case "bubbles":foreach(var b in new[]{(.25,.7,.16),(.65,.43,.22),(.35,.20,.13)})dc.DrawEllipse(null,new Pen(Glow,.045),new(b.Item1,b.Item2),b.Item3,b.Item3);break;
            case "scroll":Rect(dc,Paper,.15,.14,.7,.72,Ink,.04);dc.DrawEllipse(Light,pen,new(.5,.14),.36,.08);dc.DrawEllipse(Light,pen,new(.5,.86),.36,.08);Line(dc,Wood,.27,.37,.73,.37,.03);Line(dc,Wood,.27,.49,.73,.49,.03);break;
            case "handheld":
                dc.DrawRoundedRectangle(Ink,new Pen(Metal,.026),new(.015,.32,.97,.42),.12,.12);
                Rect(dc,B("#10151D"),.22,.35,.56,.35);Rect(dc,B("#293F62"),.26,.39,.48,.26);
                Rect(dc,B("#79B8CE"),.26,.39,.48,.11);Rect(dc,B("#75A755"),.26,.60,.48,.05);
                double player=.31+((int)(seconds*3)%4)*.05;
                Rect(dc,Gold,player,.53,.05,.07);Rect(dc,Paper,player,.50,.04,.035);
                Rect(dc,Wood,.59,.54,.075,.065);Rect(dc,Paper,.67,.42,.025,.025);
                Line(dc,Paper,.07,.52,.19,.52,.026);Line(dc,Paper,.13,.46,.13,.58,.026);
                dc.DrawEllipse((int)(seconds*5)%2==0?Rose:Gold,null,new(.86,.48),.025,.025);
                dc.DrawEllipse(Rose,null,new(.91,.54),.025,.025);dc.DrawEllipse(Metal,null,new(.12,.64),.027,.027);
                break;
            case "popcorn":
                var carton=new StreamGeometry();using(var g=carton.Open()){g.BeginFigure(new(.12,.30),true,true);g.PolyLineTo([new(.88,.30),new(.76,.96),new(.24,.96)],true,false);}carton.Freeze();
                dc.DrawGeometry(B("#FFF4DB"),new Pen(Ink,.026),carton);dc.PushClip(carton);
                for(int stripe=0;stripe<5;stripe++)Rect(dc,B("#BB3449"),.15+stripe*.17,.29,.09,.72);
                dc.Pop();dc.DrawEllipse(B("#F7D48B"),new Pen(Ink,.016),new(.5,.30),.38,.105);
                foreach(var kernel in new[]{(.18,.22),(.35,.14),(.54,.12),(.74,.20),(.32,.28),(.53,.28),(.70,.31)})
                {
                    dc.DrawEllipse(B("#EAC475"),null,new(kernel.Item1,kernel.Item2+.045),.095,.075);
                    dc.DrawEllipse(B("#FFF4CE"),null,new(kernel.Item1-.03,kernel.Item2),.075,.06);
                    dc.DrawEllipse(B("#FFF8E2"),null,new(kernel.Item1+.035,kernel.Item2-.025),.065,.07);
                    dc.DrawEllipse(B("#FFF4CE"),null,new(kernel.Item1+.06,kernel.Item2+.035),.06,.045);
                }
                break;
            case "sketchbook" or "book":Rect(dc,Cloth,.1,.1,.8,.8,Ink,.04);Rect(dc,Paper,.22,.17,.61,.65);Line(dc,Ink,.20,.11,.20,.89,.06);if(id=="sketchbook"){Line(dc,Rose,.33,.63,.60,.30,.07);Line(dc,Gold,.60,.30,.73,.59,.07);}else{Line(dc,Wood,.34,.34,.72,.34,.035);Line(dc,Wood,.34,.45,.72,.45,.035);}break;
            default:Rect(dc,Paper,.12,.16,.76,.68,Ink,.045);Line(dc,Gold,.25,.34,.75,.34,.045);Line(dc,Gold,.25,.56,.67,.56,.045);break;
        }
        dc.Pop();dc.Pop();
    }
    private static void Polygon(DrawingContext dc,Brush fill,Pen? pen,params Point[] points)
    {var g=new StreamGeometry();using(var c=g.Open()){c.BeginFigure(points[0],true,true);c.PolyLineTo(points.Skip(1).ToArray(),true,false);}g.Freeze();dc.DrawGeometry(fill,pen,g);}
    private static Brush B(string hex){if(Palette.TryGetValue(hex,out var cached))return cached;var b=(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;b.Freeze();return Palette[hex]=b;}
}
