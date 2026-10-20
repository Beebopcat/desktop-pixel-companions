using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Controls;
public partial class PortraitControl
{
    private static readonly Dictionary<BitmapSource,int> BottomInsets=[];
    private void AlignFeetToFloor()
    {
        if(!pixelSheet||Portrait.Source is not BitmapSource source)return;
        bool semantic=CompanionGroundRegistration.TryGround(source,out var semanticY);
        int inset=0;
        if(!semantic&&!BottomInsets.TryGetValue(source,out inset))
        {
            var rgba=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            int stride=rgba.PixelWidth*4;var pixels=new byte[stride*rgba.PixelHeight];rgba.CopyPixels(pixels,stride,0);
            int bottom=rgba.PixelHeight-1;
            for(;bottom>=0;bottom--)
            {
                bool visible=false;for(int x=0;x<rgba.PixelWidth;x++)if(pixels[bottom*stride+x*4+3]>96){visible=true;break;}
                if(visible)break;
            }
            inset=bottom<0?0:rgba.PixelHeight-bottom-1;BottomInsets[source]=inset;
        }
        double scale=Math.Min(Portrait.ActualWidth/source.PixelWidth,Portrait.ActualHeight/source.PixelHeight);
        if(!double.IsFinite(scale)||scale<=0)return;
        // Image measures to its Uniform-fit size, so its centered layout offset
        // also belongs in the correction. Map the last visible source edge into
        // the actual row-zero stage after clearing our previous adjustment.
        FrameAlignment.Y=0;
        double contentBottom=(Portrait.ActualHeight-source.PixelHeight*scale)/2+(semantic?semanticY:source.PixelHeight-inset)*scale;
        var visibleEdge=Portrait.TransformToVisual(PropLayer).Transform(new System.Windows.Point(Portrait.ActualWidth/2,contentBottom));
        if(PropLayer.ActualHeight>0&&double.IsFinite(visibleEdge.Y))
            FrameAlignment.Y=Math.Round(PropLayer.ActualHeight-visibleEdge.Y);
    }
}
