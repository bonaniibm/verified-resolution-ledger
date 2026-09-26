using System.Text.Json;
using Vrl.Core.Model;

namespace Vrl.Dataverse.Adapters;

/// <summary>What a Copilot Studio transcript says about one bot session.</summary>
/// <param name="ConversationId">Omnichannel conversation (msdyn_ocliveworkitem) the session served, when present.</param>
/// <param name="Topic">Last business topic the agent recognised (courtesy and system topics are skipped).</param>
/// <param name="Topics">Every recognised topic, in order.</param>
/// <param name="CopilotOutcome">Copilot Studio's own session outcome (e.g. Resolved, Escalated, Abandoned).</param>
public sealed record CopilotSessionInfo(
    Guid? ConversationId,
    string? Topic,
    IReadOnlyList<string> Topics,
    string? CopilotOutcome,
    string? OutcomeReason,
    bool ImpliedSuccess,
    int TurnCount);

/// <summary>
/// Parses the JSON 'content' of a Copilot Studio <c>conversationtranscript</c> row.
/// Validated against real transcripts of an agent connected to Dynamics 365 Contact Center:
/// <list type="bullet">
/// <item>the <c>startConversation</c> / <c>setcontext</c> events carry <c>msdyn_liveworkitemid</c> (= the Omnichannel conversation id);</item>
/// <item>topics are traced as <c>IntentRecognition</c> activities with <c>intentTitle</c>
///       (older agents emit <c>TopicStart</c>/<c>DialogRedirect</c> with a <c>targetDialogId</c>; both are read);</item>
/// <item>a <c>SessionInfo</c> trace carries the agent's own outcome, reason, implied success and turn count.</item>
/// </list>
/// The format is not a formal contract, so parsing is tolerant: unknown shapes yield nulls, never exceptions.
/// Transcripts are written when the Copilot Studio session ends (about 30 minutes after the conversation started
/// in our tests), i.e. usually after the conversation has already been ingested.
/// </summary>
public static class CopilotStudioTranscriptParser
{
    /// <summary>Courtesy and system topics that say nothing about what the customer needed.</summary>
    public static readonly IReadOnlySet<string> NonBusinessTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Greeting", "Thank you", "Goodbye", "Start Over", "Start over", "Reset Conversation", "End of Conversation",
        "Escalate", "Fallback", "Multiple Topics Matched", "On Error", "Sign in", "Conversation Start",
        "Conversational boosting",
    };

    public static CopilotSessionInfo? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("activities", out var activities) || activities.ValueKind != JsonValueKind.Array)
                return null;

            Guid? conversationId = null;
            var topics = new List<string>();
            string? outcome = null, reason = null;
            var implied = false;
            var turns = 0;

            foreach (var a in activities.EnumerateArray())
            {
                var type = Str(a, "type");
                var valueType = Str(a, "valueType");
                var value = a.TryGetProperty("value", out var v) ? v : default;

                if (type == "event" && conversationId is null && value.ValueKind == JsonValueKind.Object)
                {
                    var id = Str(value, "msdyn_liveworkitemid") ?? Str(value, "msdyn_ConversationId");
                    if (Guid.TryParse(id, out var g)) conversationId = g;
                }

                switch (valueType)
                {
                    case "IntentRecognition" when value.ValueKind == JsonValueKind.Object:
                        if (Str(value, "intentTitle") is { Length: > 0 } title) topics.Add(title);
                        break;
                    case "TopicStart" or "DialogRedirect" when value.ValueKind == JsonValueKind.Object:
                        if (SimplifyDialogId(Str(value, "targetDialogId")) is { } dialog) topics.Add(dialog);
                        break;
                    case "SessionInfo" when value.ValueKind == JsonValueKind.Object:
                        outcome = Str(value, "outcome");
                        reason = Str(value, "outcomeReason");
                        implied = value.TryGetProperty("impliedSuccess", out var i) && i.ValueKind == JsonValueKind.True;
                        turns = value.TryGetProperty("turnCount", out var t) && t.TryGetInt32(out var tc) ? tc : 0;
                        break;
                }
            }

            var business = topics.LastOrDefault(t => !NonBusinessTopics.Contains(t));
            return new CopilotSessionInfo(conversationId, business, topics, outcome, reason, implied, turns);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>"auto_agent_Y6JvM.topic.CheckRefundStatus" → "CheckRefundStatus".</summary>
    private static string? SimplifyDialogId(string? dialogId)
    {
        if (string.IsNullOrWhiteSpace(dialogId)) return null;
        var idx = dialogId.LastIndexOf(".topic.", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? dialogId[(idx + 7)..] : dialogId;
    }
}
