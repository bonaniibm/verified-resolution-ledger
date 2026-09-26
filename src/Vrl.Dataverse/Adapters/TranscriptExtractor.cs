using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vrl.Dataverse.Adapters;

/// <summary>
/// Tolerant extraction of human-readable message text from an Omnichannel transcript JSON document, in conversation
/// order. Omnichannel stores messages newest-first; when messages carry a timestamp they are sorted by it, otherwise
/// the stored order is reversed.
/// </summary>
public static partial class TranscriptExtractor
{
    public const int MaxChars = 2000;

    /// <summary>All human-readable messages (customer, bot, representative), in conversation order.</summary>
    public static string? Extract(string json) => Join(Parse(json), customerOnly: false);

    /// <summary>
    /// Only the customer's own messages, in order. Omnichannel marks every message with <c>isFromAgent</c> (true for
    /// both AI agents and representatives); system notices carry the <c>system</c> tag. Returns null when the
    /// transcript does not identify senders, so callers fall back to the full text.
    /// </summary>
    public static string? ExtractCustomer(string json) => Join(Parse(json), customerOnly: true);

    private static List<Part>? Parse(string json)
    {
        var parts = new List<Part>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, parts, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
        return parts;
    }

    private static string? Join(List<Part>? parts, bool customerOnly)
    {
        if (parts is null) return null;
        if (customerOnly) parts = parts.Where(p => p.FromCustomer == true).ToList();

        var timed = parts.Count(p => p.At is not null);
        IEnumerable<Part> ordered = timed > 0 && timed >= parts.Count / 2
            ? parts.OrderBy(p => p.At ?? DateTimeOffset.MaxValue).ThenBy(p => p.Seq)
            : Enumerable.Reverse(parts);

        var sb = new StringBuilder();
        foreach (var p in ordered.Select(p => Clean(p.Text)).Where(p => p.Length > 0).Distinct())
        {
            if (sb.Length + p.Length + 1 > MaxChars) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <param name="FromCustomer">true = customer, false = AI agent or representative, null = sender unknown.</param>
    private sealed record Part(string Text, DateTimeOffset? At, int Seq, bool? FromCustomer);

    private static readonly string[] TimeProps = ["created", "createdDateTime", "createdOn", "timestamp", "originalarrivaltime", "OriginalArrivalTime"];

    private static DateTimeOffset? TimeOf(JsonElement obj)
    {
        foreach (var name in TimeProps)
            if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var t))
                return t;
        return null;
    }

    private static void Walk(JsonElement e, List<Part> parts, int depth)
    {
        if (depth > 12) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) Walk(x, parts, depth + 1);
                break;
            case JsonValueKind.Object:
                var isControl = e.TryGetProperty("isControlMessage", out var ic) && ic.ValueKind == JsonValueKind.True;
                var tags = e.TryGetProperty("tags", out var t) ? t.ToString() : "";
                var isSystem = tags.Contains("system", StringComparison.OrdinalIgnoreCase);
                foreach (var prop in e.EnumerateObject())
                {
                    if (prop.NameEquals("content") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = prop.Value.GetString()!;
                        if (LooksLikeJson(v)) Nested(v, parts, depth);
                        else if (!isControl && !isSystem)
                        {
                            bool? fromCustomer = e.TryGetProperty("isFromAgent", out var fa) && fa.ValueKind is JsonValueKind.True or JsonValueKind.False
                                ? fa.ValueKind == JsonValueKind.False
                                : tags.Contains("FromCustomer", StringComparison.OrdinalIgnoreCase) ? true : null;
                            parts.Add(new Part(v, TimeOf(e), parts.Count, fromCustomer));
                        }
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Walk(prop.Value, parts, depth + 1);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String && LooksLikeJson(prop.Value.GetString()!))
                    {
                        Nested(prop.Value.GetString()!, parts, depth); // some transcripts nest the message list as a JSON string
                    }
                }
                break;
        }
    }

    private static void Nested(string s, List<Part> parts, int depth)
    {
        try
        {
            using var inner = JsonDocument.Parse(s);
            Walk(inner.RootElement, parts, depth + 1);
        }
        catch (JsonException) { /* not JSON after all */ }
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith('[') || t.StartsWith('{');
    }

    private static string Clean(string s) =>
        Whitespace().Replace(WebUtility.HtmlDecode(Html().Replace(s, " ")), " ").Trim();

    [GeneratedRegex("<[^>]+>")] private static partial Regex Html();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
