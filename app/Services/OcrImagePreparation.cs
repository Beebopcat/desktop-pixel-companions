using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzerothCompanion.Services;

internal sealed record OcrPreparedImage(byte[] Pixels, int Width, int Height);

internal static class OcrImagePreparation
{
    // A second pass never magnifies beyond two times or allocates an image above
    // this pixel budget. It does not tile or start another OCR/model worker.
    private const int MaximumEnhancedPixels = 8_388_608;

    internal static OcrPreparedImage Resize(CapturedFrame frame, double requestedScale, uint maximumDimension)
    {
        double scale = Math.Min(requestedScale, (double)maximumDimension / Math.Max(frame.Width, frame.Height));
        if (Math.Abs(scale - 1) < .001) return new(frame.BgraPixels, frame.Width, frame.Height);
        var scaled = new TransformedBitmap(frame.ToBitmapSource(), new ScaleTransform(scale, scale));
        var pixels = new byte[checked(scaled.PixelWidth * scaled.PixelHeight * 4)];
        scaled.CopyPixels(pixels, scaled.PixelWidth * 4, 0);
        return new(pixels, scaled.PixelWidth, scaled.PixelHeight);
    }

    internal static OcrPreparedImage? TryEnhance(CapturedFrame frame, IReadOnlyList<OcrTextLine> baseline,
        uint maximumDimension, CancellationToken ct)
    {
        var histogram = new int[256];
        int pixelCount = checked(frame.Width * frame.Height);
        if (pixelCount > MaximumEnhancedPixels) return null;
        for (int i = 0; i < frame.BgraPixels.Length; i += 4)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            var p = frame.BgraPixels;
            histogram[(p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8]++;
        }
        int low = Percentile(histogram, pixelCount, .03), high = Percentile(histogram, pixelCount, .97);
        if (high - low < 16)
        {
            // A small dialogue block can occupy far less than 3% of the image.
            // Percentiles alone would mistake it for a blank panel. Ignore isolated
            // pixel noise but retain minor foreground colors when finding contrast.
            int minimumSamples = Math.Max(8, pixelCount / 1_000_000);
            low = Array.FindIndex(histogram, count => count >= minimumSamples);
            high = Array.FindLastIndex(histogram, count => count >= minimumSamples);
        }
        if (high - low < 16) return null; // A flat/blank frame does not justify amplifying noise.
        var heights = baseline.Where(line => !line.Bounds.IsEmpty).Select(line => line.Bounds.Height).Order().ToArray();
        bool smallText = heights.Length > 0 && heights[heights.Length / 2] < 22;
        bool sparse = baseline.Sum(line => line.Text.Count(char.IsLetterOrDigit)) < 40;
        bool dark = Percentile(histogram, pixelCount, .5) < 110;
        bool lowContrast = high - low < 160;
        if (!smallText && !sparse && !dark && !lowContrast) return null;
        double scale = smallText || sparse ? Math.Min(2, Math.Sqrt((double)MaximumEnhancedPixels / pixelCount)) : 1;
        var pixels = frame.BgraPixels.ToArray();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if ((i & 65535) == 0) ct.ThrowIfCancellationRequested();
            int luminance = (pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8;
            int normalized = Math.Clamp((luminance - low) * 255 / (high - low), 0, 255);
            // Supply dark glyphs on a light background for dark game panels. Keeping
            // original OCR as the primary pass protects color-only text distinctions.
            byte value = (byte)(dark ? 255 - normalized : normalized);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }
        return Resize(frame with { BgraPixels = pixels }, scale, maximumDimension);
    }

    private static int Percentile(int[] histogram, int total, double fraction)
    {
        int accumulated = 0, threshold = (int)Math.Ceiling(total * fraction);
        for (int i = 0; i < histogram.Length; i++)
            if ((accumulated += histogram[i]) >= threshold) return i;
        return 255;
    }
}
