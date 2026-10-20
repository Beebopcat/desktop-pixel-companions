namespace AzerothCompanion.Core;
public static class OverlaySizing
{
    public static (int Width,int Height) Calculate(int monitorWidth,int monitorHeight,bool duo,double fraction=.04)
    {
        if(monitorWidth<=0||monitorHeight<=0)throw new ArgumentOutOfRangeException(nameof(monitorWidth));
        fraction=double.IsFinite(fraction)?Math.Clamp(fraction,.015,.045):.04;
        double area=(double)monitorWidth*monitorHeight*fraction,aspect=duo?1.85:1.0;
        int width=Math.Max(1,(int)Math.Floor(Math.Min(monitorWidth,Math.Sqrt(area*aspect))));
        int height=Math.Max(1,(int)Math.Floor(Math.Min(monitorHeight,area/width)));
        return(width,height);
    }
}
