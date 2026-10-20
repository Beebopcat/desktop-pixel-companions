using System.Windows;

namespace AzerothCompanion.Services;

internal static class OcrLineMerger
{
    internal static IReadOnlyList<OcrTextLine> Merge(IReadOnlyList<OcrTextLine> original, IReadOnlyList<OcrTextLine> enhanced,
        bool preferEnhancedWhenSimilar = false)
    {
        var result = original.ToList();
        foreach (var candidate in enhanced)
        {
            if (candidate.Bounds.IsEmpty || candidate.Text.Count(char.IsLetter) < 2) continue;
            int overlapIndex = result.FindIndex(line => SamePosition(line.Bounds, candidate.Bounds));
            if (overlapIndex >= 0)
            {
                var previous = result[overlapIndex];
                // Do not prefer a longer string just because a second pass hallucinated
                // punctuation. Only accept a clear gain in readable words at this position.
                if ((ReadableWords(candidate.Text) > ReadableWords(previous.Text) &&
                    candidate.Text.Count(char.IsLetter) > previous.Text.Count(char.IsLetter) * 1.2) ||
                    (preferEnhancedWhenSimilar && ReadableWords(candidate.Text) >= ReadableWords(previous.Text) &&
                    SimilarText(previous.Text, candidate.Text)))
                    result[overlapIndex] = candidate;
                continue;
            }
            if (ReadableWords(candidate.Text) == 0) continue;
            // Keep the native reading/column order. Insert newly recovered lines near
            // their vertical neighbor in the same column instead of sorting all columns
            // together by Y, which interleaves unrelated text panels.
            var sameColumn = result.Select((line, index) => (line, index))
                .Where(item => HorizontalOverlap(item.line.Bounds, candidate.Bounds) >= .45).ToArray();
            var below = sameColumn.Where(item => item.line.Bounds.Top >= candidate.Bounds.Bottom - 2)
                .OrderBy(item => item.line.Bounds.Top).FirstOrDefault();
            if (below.line != null) result.Insert(below.index, candidate);
            else
            {
                var above = sameColumn.Where(item => item.line.Bounds.Bottom <= candidate.Bounds.Top + 2)
                    .OrderByDescending(item => item.line.Bounds.Bottom).FirstOrDefault();
                if (above.line != null) result.Insert(above.index + 1, candidate);
                else result.Add(candidate);
            }
        }
        return result;
    }

    private static bool SamePosition(Rect a, Rect b)
    {
        double vertical = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return vertical / Math.Max(1, Math.Min(a.Height, b.Height)) >= .5 && HorizontalOverlap(a, b) >= .55;
    }
    private static double HorizontalOverlap(Rect a, Rect b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) / Math.Max(1, Math.Min(a.Width, b.Width));
    private static int ReadableWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Count(word => word.Count(char.IsLetter) >= 2 && word.Count(char.IsLetter) >= word.Length * .65);

    private static bool SimilarText(string left, string right)
    {
        // An upscaled glyph can fix a letter without changing the word count. Require
        // close text agreement as well as matching geometry before preferring it.
        left = new string(left.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Take(512).ToArray());
        right = new string(right.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Take(512).ToArray());
        if (left.Length == 0 || right.Length == 0) return false;
        var row = Enumerable.Range(0, right.Length + 1).ToArray();
        for (int i = 1; i <= left.Length; i++)
        {
            int diagonal = row[0]; row[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int previous = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (left[i - 1] == right[j - 1] ? 0 : 1));
                diagonal = previous;
            }
        }
        return row[^1] <= Math.Max(left.Length, right.Length) * .22;
    }
}
