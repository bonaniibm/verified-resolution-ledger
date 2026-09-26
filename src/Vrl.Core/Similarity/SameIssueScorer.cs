using Vrl.Core.Model;
using Vrl.Core.Policy;

namespace Vrl.Core.Similarity;

/// <summary>One piece of evidence and how much it moved the score. Persisted as JSON for explainability.</summary>
public sealed record SignalContribution(string Signal, double Contribution, string Detail);

public sealed record SameIssueAssessment(double Score, bool IsSameIssue, IReadOnlyList<SignalContribution> Signals)
{
    public static SameIssueAssessment None { get; } = new(0, false, Array.Empty<SignalContribution>());
}

/// <summary>
/// Explainable, additive scoring of whether a later interaction is about the same issue as an earlier one.
/// Deliberately not a black-box model: every verdict must be defensible to a contact-centre director and a works council.
/// </summary>
public sealed class SameIssueScorer
{
    private readonly LedgerPolicy _policy;
    private readonly ReferenceExtractor _references;

    public SameIssueScorer(LedgerPolicy policy, ReferenceExtractor? references = null)
    {
        _policy = policy;
        _references = references ?? ReferenceExtractor.Default;
    }

    public SameIssueAssessment Score(Interaction earlier, Interaction later)
    {
        var w = _policy.Weights;
        var signals = new List<SignalContribution>(5);

        if (earlier.CaseId is { } c1 && later.CaseId is { } c2 && c1 == c2 && c1 != Guid.Empty)
            signals.Add(new("SameCase", w.SameCase, $"Both linked to case {c1:D}"));

        var intentA = Norm(earlier.IntentCode);
        var intentB = Norm(later.IntentCode);
        var famA = Norm(earlier.IntentFamily) ?? FamilyOf(intentA);
        var famB = Norm(later.IntentFamily) ?? FamilyOf(intentB);

        if (intentA is not null && intentA == intentB)
            signals.Add(new("IntentExact", w.IntentExact, $"Intent '{intentA}'"));
        else if (famA is not null && famA == famB)
            signals.Add(new("IntentFamily", w.IntentFamily, $"Intent family '{famA}'"));
        else if (famA is not null && famB is not null && famA != famB)
            signals.Add(new("IntentConflict", w.IntentConflictPenalty, $"'{famA}' vs '{famB}'"));

        var refsA = _references.Extract(earlier.Summary);
        if (refsA.Count > 0)
        {
            // The customer's own phone number is not an issue reference. Compare on the last 9 digits so that
            // national (0151…) and international (+49151…) formats of the same number are both excluded.
            var phoneTails = new[] { earlier.Customer.Phone, later.Customer.Phone }
                .Select(p => new string((p ?? "").Where(char.IsDigit).ToArray()))
                .Where(p => p.Length >= 7)
                .Select(Tail)
                .ToHashSet();
            var shared = _references.Extract(later.Summary)
                .Where(refsA.Contains)
                .Where(r => !(r.All(char.IsDigit) && phoneTails.Contains(Tail(r))))
                .ToList();
            if (shared.Count > 0)
            {
                var structured = shared.Where(ReferenceExtractor.IsStructured).ToList();
                signals.Add(structured.Count > 0
                    ? new("SharedReference", w.SharedStructuredReference, $"Shared structured reference(s): {string.Join(", ", structured.Take(3))}")
                    : new("SharedReference", w.SharedReference, $"Shared reference(s): {string.Join(", ", shared.Take(3))}"));
            }
        }

        if (earlier.Embedding is { Length: > 0 } ea && later.Embedding is { Length: > 0 } eb && ea.Length == eb.Length
            && string.Equals(earlier.EmbeddingModel, later.EmbeddingModel, StringComparison.Ordinal))
        {
            var cos = VectorMath.Cosine(ea, eb);
            if (cos > w.SemanticFloor)
            {
                var contribution = w.Semantic * (cos - w.SemanticFloor) / (1 - w.SemanticFloor);
                signals.Add(new("Semantic", Math.Round(contribution, 4), $"Summary cosine {cos:F3}"));
            }
        }

        var score = Math.Clamp(signals.Sum(s => s.Contribution), 0, 1);
        score = Math.Round(score, 4);
        return new SameIssueAssessment(score, score >= _policy.SameIssueThreshold, signals);
    }

    private static string Tail(string digits) => digits.Length > 9 ? digits[^9..] : digits;

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();

    /// <summary>Derives a family from dotted/slashed intent codes ("billing.refund.status" → "billing").</summary>
    private static string? FamilyOf(string? intent)
    {
        if (intent is null) return null;
        var idx = intent.IndexOfAny(['.', '/', ':']);
        return idx > 0 ? intent[..idx] : null;
    }
}
