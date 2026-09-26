using System.Text.RegularExpressions;

namespace Vrl.Core.Similarity;

/// <summary>
/// Pulls business references (order numbers, ticket IDs, invoice numbers, VINs, tracking numbers) out of free text.
/// A shared reference between two contacts is strong, explainable evidence they concern the same issue.
/// Patterns are intentionally generic; add tenant-specific ones via the constructor.
/// </summary>
public sealed partial class ReferenceExtractor
{
    private readonly IReadOnlyList<Regex> _patterns;

    public ReferenceExtractor(IEnumerable<Regex>? additionalPatterns = null)
    {
        var list = new List<Regex> { PrefixedId(), LongNumber(), Vin() };
        if (additionalPatterns is not null) list.AddRange(additionalPatterns);
        _patterns = list;
    }

    public static ReferenceExtractor Default { get; } = new();

    public IReadOnlySet<string> Extract(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;
        text = NormalizeDashes(text);
        foreach (var p in _patterns)
            foreach (Match m in p.Matches(text))
                set.Add(Canonical(m.Value));
        return set;
    }

    /// <summary>
    /// Chat clients, copy/paste and auto-correct turn "-" into typographic dashes (U+2010–2015, U+2212, U+FE58/63,
    /// U+FF0D). Fold them to ASCII so "ORD‑4455667" and "ORD-4455667" are the same reference.
    /// </summary>
    internal static string NormalizeDashes(string text)
    {
        if (!text.Any(c => c is >= '\u2010' and <= '\u2015' or '\u2212' or '\uFE58' or '\uFE63' or '\uFF0D')) return text;
        return string.Create(text.Length, text, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = src[i] is >= '\u2010' and <= '\u2015' or '\u2212' or '\uFE58' or '\uFE63' or '\uFF0D' ? '-' : src[i];
        });
    }

    /// <summary>True for references that carry a business prefix or structure (ORD-…, TRK-…, VINs), not bare digits.</summary>
    public static bool IsStructured(string canonical) => canonical.Any(char.IsLetter);

    private static string Canonical(string raw) =>
        new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // Upper-case prefixes only, so prose like "on 0151..." is not mistaken for a reference.
    // e.g. ORD-123456, INV 88812, CAS-01234-X1Y2Z3, TKT#44321
    [GeneratedRegex(@"\b[A-Z]{2,5}[-#\s]?\d{4,}(?:-[A-Za-z0-9]{2,})*\b")]
    private static partial Regex PrefixedId();

    // Bare numbers of 7-12 digits (tracking / account / order numbers). The customer's own phone number is excluded by SameIssueScorer.
    [GeneratedRegex(@"\b\d{7,12}\b")]
    private static partial Regex LongNumber();

    // Vehicle identification numbers (17 chars, no I/O/Q).
    [GeneratedRegex(@"\b[A-HJ-NPR-Z0-9]{17}\b")]
    private static partial Regex Vin();
}
