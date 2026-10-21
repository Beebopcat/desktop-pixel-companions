using System.Text.RegularExpressions;

namespace AzerothCompanion.Core;

/// <summary>Selects a bounded, verbatim span. It never invents an answer or a speaker.</summary>
public static class GameEvidenceExcerpt
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    { "the", "and", "for", "with", "from", "this", "that", "what", "how", "does", "are", "can", "game", "about", "into", "your" };

    public static string Select(string plain, string topic, int maximum = 1200)
    {
        if (maximum is < 100 or > 4000) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (plain.Length <= maximum) return plain;
        var terms = Regex.Matches(topic, @"[\p{L}\p{N}]{3,}", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            .Select(m => m.Value).Where(t => !StopWords.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        if (terms.Length == 0) return plain[..maximum];
        var hits = new List<(int Position, int Term)>();
        for (int term = 0; term < terms.Length; term++)
        {
            var matches = Regex.Matches(plain, @"(?<![\p{L}\p{N}])" + Regex.Escape(terms[term]) + @"(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            foreach (Match match in matches.Take(64)) hits.Add((match.Index, term));
        }
        int bestStart = 0, bestScore = 0;
        foreach (var hit in hits.OrderBy(h => h.Position))
        {
            int start = Math.Clamp(hit.Position - maximum / 3, 0, Math.Max(0, plain.Length - maximum));
            // Start at a nearby sentence boundary when one fits before the evidence.
            int boundary = plain.LastIndexOf(". ", Math.Min(start, plain.Length - 1), Math.Min(start + 1, 160), StringComparison.Ordinal);
            if (boundary >= 0) start = boundary + 2;
            var visible = hits.Where(h => h.Position >= start && h.Position < start + maximum).ToArray();
            int score = visible.Select(h => h.Term).Distinct().Count() * 100 + Math.Min(visible.Length, 20);
            if (score > bestScore) { bestScore = score; bestStart = start; }
        }
        return plain.Substring(bestStart, Math.Min(maximum, plain.Length - bestStart));
    }
}
