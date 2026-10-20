using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AzerothCompanion.Services;

public sealed record OcrTextLine(string Text, Rect Bounds);
public sealed record OcrResult(string Text, IReadOnlyList<OcrTextLine> Lines, string Language);

/// <summary>On-device Windows OCR only. Does not upload images or download language packs.</summary>
public sealed class OcrService
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    public IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray(); }
            catch { return Array.Empty<string>(); }
        }
    }

    public async Task<OcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            OcrEngine? engine;
            try
            {
                engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null)
                {
                    var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
                    if (language != null) engine = OcrEngine.TryCreateFromLanguage(language);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Windows OCR could not initialize. Check Windows language OCR support; this capture can still be used without OCR.", ex);
            }
            if (engine == null) throw new InvalidOperationException("No Windows OCR language is installed. Add an OCR-capable language in Windows Settings, or continue without OCR.");
            var original = OcrImagePreparation.Resize(frame, 1, OcrEngine.MaxImageDimension);
            var lines = await RecognizePassAsync(engine, original, frame.Width, frame.Height, ct).ConfigureAwait(false);
            // One bounded fallback only. Windows OCR can miss low-contrast colored glyphs
            // and small game fonts even when the selected-window capture is correct.
            var enhanced = OcrImagePreparation.TryEnhance(frame, lines, OcrEngine.MaxImageDimension, ct);
            if (enhanced != null)
            {
                var fallback = await RecognizePassAsync(engine, enhanced, frame.Width, frame.Height, ct).ConfigureAwait(false);
                lines = OcrLineMerger.Merge(lines, fallback, enhanced.Width > original.Width);
            }
            ct.ThrowIfCancellationRequested();
            // The native Text property flattens line breaks, losing dialogue structure
            // and making line-based quest-title extraction impossible.
            return new(string.Join("\n", lines.Select(line => line.Text)), lines, engine.RecognizerLanguage.LanguageTag);
        }
        finally { _serial.Release(); }
    }

    private static async Task<IReadOnlyList<OcrTextLine>> RecognizePassAsync(OcrEngine engine,
        OcrPreparedImage image, int originalWidth, int originalHeight, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(image.Pixels.AsBuffer());
        var recognized = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        double scaleX = (double)originalWidth / image.Width, scaleY = (double)originalHeight / image.Height;
        // Windows returns deskewed coordinates. Rotate boxes clockwise by TextAngle,
        // around the image center, before mapping them back to capture coordinates.
        // https://learn.microsoft.com/uwp/api/windows.media.ocr.ocrresult.textangle
        var rotation = new RotateTransform(recognized.TextAngle ?? 0, image.Width / 2d, image.Height / 2d);
        return recognized.Lines.Select(line =>
        {
            var box = Rect.Empty;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                var positioned = rotation.TransformBounds(new Rect(r.X, r.Y, r.Width, r.Height));
                box.Union(new Rect(positioned.X * scaleX, positioned.Y * scaleY, positioned.Width * scaleX, positioned.Height * scaleY));
            }
            box.Intersect(new Rect(0, 0, originalWidth, originalHeight));
            return new OcrTextLine(line.Text.Trim(), box);
        }).Where(line => line.Text.Length > 0 && !line.Bounds.IsEmpty).ToArray();
    }
}
