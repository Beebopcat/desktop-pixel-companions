using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Services;

public sealed record WindowChoice(nint Handle, uint ProcessId, string Title, string ProcessName)
{
    public nint Hwnd => Handle;
    public string DisplayName => $"{Title} — {ProcessName}";
    public override string ToString() => DisplayName;
}

public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height)
{
    public static NormalizedRegion Full => new(0, 0, 1, 1);
    internal void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 1.000001 || Y + Height > 1.000001)
            throw new ArgumentOutOfRangeException(nameof(NormalizedRegion), "Region must fit inside the selected window (values from 0 to 1).");
    }
}

public sealed record CapturedFrame(int Width, int Height, byte[] BgraPixels, DateTimeOffset CapturedAt, string WindowTitle)
{
    public int OriginX { get; init; }
    public int OriginY { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }

    public BitmapSource ToBitmapSource()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, BgraPixels, checked(Width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    // Encoding stays in memory. Saving or sending a frame is a separate explicit caller action.
    public byte[] ToPng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource()));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public CapturedFrame Crop(NormalizedRegion region)
    {
        region.Validate();
        int left = Math.Clamp((int)Math.Floor(region.X * Width), 0, Width - 1);
        int top = Math.Clamp((int)Math.Floor(region.Y * Height), 0, Height - 1);
        int right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * Width), left + 1, Width);
        int bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * Height), top + 1, Height);
        int width = right - left, height = bottom - top;
        var pixels = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(BgraPixels, checked(((top + row) * Width + left) * 4), pixels, row * width * 4, width * 4);
        return new(width, height, pixels, CapturedAt, WindowTitle)
        {
            OriginX = OriginX + left, OriginY = OriginY + top,
            SourceWidth = SourceWidth == 0 ? Width : SourceWidth,
            SourceHeight = SourceHeight == 0 ? Height : SourceHeight
        };
    }
}
