using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzerothCompanion.Controls;
using AzerothCompanion.Core;
namespace AzerothCompanion;
public static partial class VisualPreview
{
    public static async Task RenderOverlayAsync(string path)
    {
        var window=new OverlayWindow();
        try
        {
            window.SetProfile(Characters.All[0]);window.SetPartner(Characters.Find("jaina"),true);window.ConfigureFootprint(0,.04);window.Show();window.SetState("idle");await Task.Delay(250);
            // Render only our own avatar visuals at their actual desktop positions.
            // No desktop/game screenshot and no image of the invisible controller.
            var evidence=System.Text.Json.JsonSerializer.SerializeToElement(window.GeometryEvidence());
            var work=evidence.GetProperty("primaryWorkArea");var left=work.GetProperty("Left").GetInt32();var top=work.GetProperty("Top").GetInt32();
            var width=work.GetProperty("Width").GetInt32();var height=work.GetProperty("Height").GetInt32();
            var bounds=evidence.GetProperty("individualBounds").EnumerateArray().ToArray();var avatars=window.AvatarVisuals;
            var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())
            {
                if(window.FloorVisual is { } floorVisual)
                {
                    floorVisual.UpdateLayout();var floor=evidence.GetProperty("floor").GetProperty("bounds");
                    drawing.DrawRectangle(ElementBrush(floorVisual),null,new Rect(floor.GetProperty("Left").GetInt32()-left,floor.GetProperty("Top").GetInt32()-top,floor.GetProperty("Width").GetInt32(),floor.GetProperty("Height").GetInt32()));
                }
                for(int i=0;i<Math.Min(avatars.Count,bounds.Length);i++)
                {
                    avatars[i].UpdateLayout();var b=bounds[i];
                    drawing.DrawRectangle(ElementBrush(avatars[i]),null,new Rect(b.GetProperty("left").GetInt32()-left,b.GetProperty("top").GetInt32()-top,b.GetProperty("width").GetInt32(),b.GetProperty("height").GetInt32()));
                }
            }
            var bmp=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bmp.Render(visual);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));
            using(var stream=File.Create(path))encoder.Save(stream);
            File.WriteAllText(Path.ChangeExtension(path,"json"),System.Text.Json.JsonSerializer.Serialize(evidence,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
        finally{window.Close();}
    }
    public static async Task RenderGalleryAsync(string path)
    {
        var root=new StackPanel{Width=1160,Margin=new Thickness(32),Background=new SolidColorBrush(Color.FromRgb(12,17,27))};
        root.Children.Add(new TextBlock{Text="YOUR AZEROTH COMPANIONS",FontSize=12,Foreground=Brushes.Tan});
        root.Children.Add(new TextBlock{Text="Choose your traveling pair.",FontFamily=new FontFamily("Georgia"),FontSize=28,Margin=new Thickness(0,10,0,8)});
        root.Children.Add(new TextBlock{Text="Retro pixel-art sprites · Full-body poses, blinking, gestures and audio-driven mouths",Foreground=Brushes.LightSlateGray,FontSize=14,Margin=new Thickness(0,0,0,22)});
        var row=new Grid{Width=1160};for(int i=0;i<Characters.All.Length;i++)row.ColumnDefinitions.Add(new());
        int column=0;foreach(var profile in Characters.All)
        {
            var stack=new StackPanel();stack.Children.Add(new TextBlock{Text=profile.Faction,FontSize=11,Foreground=(Brush)new BrushConverter().ConvertFromString(profile.Accent)!});
            var portrait=new PortraitControl{Height=255};portrait.Compact();portrait.SetCharacter(profile);stack.Children.Add(portrait);
            stack.Children.Add(new TextBlock{Text=profile.Name,FontSize=20,FontFamily=new FontFamily("Georgia"),Margin=new Thickness(0,8,0,6)});
            stack.Children.Add(new TextBlock{Text=profile.Role,FontSize=12,Foreground=Brushes.LightSlateGray});
            var card=new Border{Child=stack,Padding=new Thickness(18),Margin=new Thickness(0,0,12,0),CornerRadius=new CornerRadius(12),Background=new SolidColorBrush(Color.FromRgb(21,29,41))};Grid.SetColumn(card,column++);row.Children.Add(card);
        }
        root.Children.Add(row);
        root.Children.Add(new TextBlock{Text="In game: one compact panel for both companions, capped at 4.5% of the monitor. Each character uses its assigned installed voice for reading and replies.",Margin=new Thickness(0,22,0,0),FontSize=13,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSlateGray});
        var window=new Window{Content=root,Width=1224,Height=600,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Background=new SolidColorBrush(Color.FromRgb(12,17,27)),Foreground=Brushes.White};
        window.Show();await Task.Delay(250);SaveElement(root,path);window.Close();
    }
    public static void SaveElement(FrameworkElement element,string path)
    {
        element.UpdateLayout();var bmp=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
        var visual=new DrawingVisual();using(var drawing=visual.RenderOpen())drawing.DrawRectangle(ElementBrush(element),null,new Rect(0,0,element.ActualWidth,element.ActualHeight));bmp.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);using var stream=File.Create(path);encoder.Save(stream);
    }
    private static VisualBrush ElementBrush(FrameworkElement element)=>new(element){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,element.ActualWidth,element.ActualHeight),Stretch=Stretch.Fill};
}


