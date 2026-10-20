using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzerothCompanion.Controls;
using AzerothCompanion.Core;

namespace AzerothCompanion;

public static partial class VisualPreview
{
    private static object MotionGaitChecks(string directory,string? onlyCharacter)
    {
        var gait=new CompanionGaitClock();int previous=gait.Sample(0),transitions=0;
        for(int tick=1;tick<=400;tick++)
        {
            int current=gait.Sample(tick*.06);
            if(current!=previous)
            {
                if(current!=(previous+1)%8)throw new InvalidOperationException("Gait skipped an authored stride pose.");
                transitions++;previous=current;
            }
        }
        if(transitions<154||transitions>158)throw new InvalidOperationException("Gait cadence differs from the 6.5-pose-per-second target.");
        int delayed=gait.Sample(80);
        if(delayed!=(previous+1)%8||gait.Sample(80.001)!=delayed)throw new InvalidOperationException("Delayed gait tick skipped or burst-replayed poses.");
        gait.Reset();if(gait.Sample(100)!=0)throw new InvalidOperationException("New gait did not start with its first authored pose.");
        var characters=Characters.All.Where(character=>onlyCharacter==null||character.Id==onlyCharacter).ToArray();
        var portrait=new PortraitControl{Width=188,Height=188};portrait.Compact();
        var visuals=new DrawingVisual();var frames=new List<object>();
        using(var dc=visuals.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18,23,31)),null,new Rect(0,0,8*188,characters.Length*220));
            for(int row=0;row<characters.Length;row++)
            {
                portrait.SetCharacter(characters[row]);MotionLayout(portrait);DrawText(dc,characters[row].Name,4,row*220+2,13,Brushes.White);
                for(int frame=0;frame<8;frame++)
                {
                    portrait.RenderWalkingFrameAt(frame);MotionLayout(portrait);portrait.RenderWalkingFrameAt(frame);MotionLayout(portrait);
                    var bitmap=MotionSnapshot(portrait,188);var native=MotionSnapshot(portrait,47);
                    dc.DrawImage(bitmap,new Rect(frame*188,row*220+22,188,188));
                    dc.DrawLine(new Pen(Brushes.OliveDrab,1),new(frame*188,row*220+210),new((frame+1)*188,row*220+210));
                    frames.Add(new{characterId=characters[row].Id,frame,nativeAlpha=MotionAlphaBounds(native),inspectionAlpha=MotionAlphaBounds(bitmap)});
                }
            }
        }
        var sheet=new RenderTargetBitmap(8*188,characters.Length*220,96,96,PixelFormats.Pbgra32);sheet.Render(visuals);SaveBitmap(sheet,Path.Combine(directory,"WalkingFrames.png"));
        MotionAnchorSheet(directory,characters);
        var report=new{allPassed=true,clockTransitions=transitions,delayedTickAdjacent=true,burstCatchUp=false,frames,
            inspectedByGenerationOnly=true,fullVisualRunCredit=0,windowShown=false,audioPlayed=false};
        File.WriteAllText(Path.Combine(directory,"gait-checks.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        return report;
    }
    private static void MotionAnchorSheet(string directory,CharacterProfile[] characters)
    {
        (string bank,int frame)[] poses=[("activity",11),("motion",10),("motion",11),("activity",4),("activity",6),("activity",7)];
        const int cell=256,rowHeight=292;var visual=new DrawingVisual();RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.NearestNeighbor);
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18,23,31)),null,new Rect(0,0,cell*poses.Length,rowHeight*characters.Length));
            for(int row=0;row<characters.Length;row++)for(int col=0;col<poses.Length;col++)
            {
                var(bank,frame)=poses[col];var source=SpriteAtlas.Character(characters[row].Id,bank)[frame];var rig=CompanionInteractionAssets.Rig(characters[row].Id,bank,frame);
                double x=col*cell,y=row*rowHeight+26;DrawText(dc,characters[row].Name+" / "+bank+" "+frame,x+3,row*rowHeight+2,12,Brushes.White);
                dc.DrawImage(source,new Rect(x,y,cell,cell));
                foreach(var(point,brush)in new[]{(rig.NearHand,Brushes.Yellow),(rig.FarHand,Brushes.Lime),(rig.KickFoot??rig.Feet,Brushes.Red)})
                    dc.DrawEllipse(null,new Pen(brush,1),new Point(x+point.X*cell,y+point.Y*cell),4,4);
            }
        }
        var image=new RenderTargetBitmap(cell*poses.Length,rowHeight*characters.Length,96,96,PixelFormats.Pbgra32);image.Render(visual);SaveBitmap(image,Path.Combine(directory,"InteractionAnchors.png"));
    }
}
