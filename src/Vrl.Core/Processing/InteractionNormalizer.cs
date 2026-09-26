using System.Text.RegularExpressions;
using Vrl.Core.Model;
using Vrl.Core.Util;

namespace Vrl.Core.Processing;

/// <summary>
/// Prepares an adapter-produced interaction for storage: deterministic ID, identity resolution, and data minimisation.
/// The ledger stores only a hashed customer key – never raw phone numbers or e-mail addresses – and scrubs them
/// from the summary text it keeps for similarity.
/// </summary>
public static partial class InteractionNormalizer
{
    public const int MaxSummaryLength = 4000;

    public static Interaction Normalize(Interaction source)
    {
        var id = source.Id == Guid.Empty
            ? DeterministicGuid.ForInteraction(source.SourceSystem, source.SourceRecordId)
            : source.Id;

        var (key, confidence) = source.Customer.ResolveKey();

        var summary = Redact(source.Summary, source.Customer.Phone);
        if (summary is { Length: > MaxSummaryLength }) summary = summary[..MaxSummaryLength];
        var issue = Redact(source.IssueText, source.Customer.Phone);
        if (issue is { Length: > MaxSummaryLength }) issue = issue[..MaxSummaryLength];
        if (string.IsNullOrWhiteSpace(issue)) issue = null;

        var endedOn = source.EndedOn < source.StartedOn ? source.StartedOn : source.EndedOn;

        return source with
        {
            Id = id,
            EndedOn = endedOn,
            Summary = summary,
            IssueText = issue,
            IntentCode = source.IntentCode?.Trim().ToLowerInvariant(),
            IntentFamily = (source.IntentFamily ?? FamilyOf(source.IntentCode))?.Trim().ToLowerInvariant(),
            Customer = CustomerIdentity.FromKnownKey(key, confidence, source.Customer.ContactId, source.Customer.AccountId),
        };
    }

    public static string? Redact(string? text, string? customerPhone)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var result = EmailRegex().Replace(text, "[EMAIL]");

        var phoneDigits = new string((customerPhone ?? "").Where(char.IsDigit).ToArray());
        if (phoneDigits.Length >= 7)
        {
            var tail = phoneDigits.Length > 9 ? phoneDigits[^9..] : phoneDigits;
            result = PhoneLikeRegex().Replace(result, m =>
            {
                var d = new string(m.Value.Where(char.IsDigit).ToArray());
                return d.EndsWith(tail, StringComparison.Ordinal) ? "[PHONE]" : m.Value;
            });
        }

        // Payment-card-like sequences are never kept, regardless of customer (defence in depth; PCI scope reduction).
        result = CardLikeRegex().Replace(result, "[CARD]");
        return result;
    }

    private static string? FamilyOf(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent)) return null;
        var idx = intent.IndexOfAny(['.', '/', ':']);
        return idx > 0 ? intent[..idx] : null;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\+?\d[\d\s\-()]{6,}\d")]
    private static partial Regex PhoneLikeRegex();

    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b")]
    private static partial Regex CardLikeRegex();
}
