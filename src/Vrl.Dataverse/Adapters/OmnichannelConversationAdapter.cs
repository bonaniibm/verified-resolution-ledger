using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Vrl.Core.Model;
using Vrl.Core.Processing;

namespace Vrl.Dataverse.Adapters;

public sealed class OmnichannelAdapterOptions
{
    /// <summary>Copilot credits per bot minute on voice (GenAI voice is metered per minute). Estimate – tune to your meter.</summary>
    public double VoiceCreditsPerBotMinute { get; set; } = 35;

    /// <summary>Estimated credits for a digital bot conversation (generative answers × turns). Tune from PPAC usage.</summary>
    public double DigitalCreditsPerBotConversation { get; set; } = 10;

    /// <summary>
    /// For conversations with no linked customer, link by an e-mail the customer declared (pre-chat survey or chat text)
    /// when it matches exactly one active contact. Links at Medium confidence. Turn off where self-declared identity is
    /// not acceptable for reporting; use authenticated chat instead.
    /// </summary>
    public bool IdentifyFromSelfDeclaredEmail { get; set; } = true;
}

/// <summary>
/// Maps a closed Omnichannel conversation (msdyn_ocliveworkitem) into the canonical <see cref="Interaction"/>.
///
/// Defensive by design: column availability varies by Contact Center / Customer Service version, so the requested
/// column set is intersected with live metadata, and the channel is resolved from the option *label* (formatted value)
/// rather than hard-coded option numbers. Validate the mapping in your tenant with `vrl inspect-conversation`.
/// </summary>
public sealed class OmnichannelConversationAdapter(
    IOrganizationServiceAsync2 service,
    ILogger<OmnichannelConversationAdapter> logger,
    OmnichannelAdapterOptions? options = null)
{
    public const string SourceSystem = "omnichannel";
    public const string Table = "msdyn_ocliveworkitem";

    private static readonly string[] Wanted =
    [
        "msdyn_channel", "msdyn_customer", "msdyn_createdon", "msdyn_initiatedon", "msdyn_closedon", "createdon",
        "msdyn_conversationsummaryfield", "msdyn_title", "msdyn_intent", "msdyn_cdsqueueid", "msdyn_isabandoned",
        "msdyn_isagentsession", "msdyn_activeagentid", "msdyn_conversationhandletimeinseconds", "msdyn_isoutbound",
        "regardingobjectid", "statuscode",
    ];

    private readonly OmnichannelAdapterOptions _options = options ?? new();
    private string[]? _columns;

    public async Task<Interaction?> BuildAsync(Guid conversationId, CancellationToken ct = default)
    {
        _columns ??= await AvailableColumnsAsync(ct);
        var e = await service.RetrieveAsync(Table, conversationId, new ColumnSet(_columns), ct);

        // statuscode 4 = Closed (per Microsoft's conversation metrics documentation).
        if (e.GetAttributeValue<OptionSetValue>("statuscode")?.Value is { } status && status != 4)
        {
            logger.LogInformation("Conversation {Id} not closed (status {Status}); skipping", conversationId, status);
            return null;
        }

        var channelLabel = e.FormattedValues.TryGetValue("msdyn_channel", out var lbl) ? lbl : null;
        var channel = MapChannel(channelLabel);

        var start = FirstDate(e, "msdyn_initiatedon", "msdyn_createdon", "createdon") ?? DateTime.UtcNow;
        var end = FirstDate(e, "msdyn_closedon") ?? start;

        var customer = e.GetAttributeValue<EntityReference>("msdyn_customer");
        var regarding = e.GetAttributeValue<EntityReference>("regardingobjectid");

        // Participants are classified per system user: a user with msdyn_botapplicationid is a bot, any other is a human.
        // The conversation-level flags are only a fallback, because a bot user also appears as "active agent" and can
        // accrue handle time – treating that as human involvement would hide exactly the bot-only conversations we measure.
        var activeAgent = e.GetAttributeValue<EntityReference>("msdyn_activeagentid");
        var participants = await ParticipantsAsync(conversationId, ct);
        bool botInvolved, humanInvolved;
        if (participants is { } p && (p.Bot || p.Human))
        {
            (botInvolved, humanInvolved) = (p.Bot, p.Human);
        }
        else
        {
            var activeIsBot = activeAgent is not null && await IsBotUserAsync(activeAgent.Id, ct);
            botInvolved = activeIsBot;
            humanInvolved = e.GetAttributeValue<bool>("msdyn_isagentsession")
                            || (activeAgent is not null && !activeIsBot)
                            || (!activeIsBot && e.GetAttributeValue<int>("msdyn_conversationhandletimeinseconds") > 0);
        }
        var abandoned = e.GetAttributeValue<bool>("msdyn_isabandoned");

        var mode = (botInvolved, humanInvolved) switch
        {
            (true, true) => HandlingMode.BotThenHuman,
            (true, false) => HandlingMode.BotOnly,
            (false, true) => HandlingMode.HumanOnly,
            _ => HandlingMode.Unknown,
        };

        var nativeOutcome = mode switch
        {
            HandlingMode.BotOnly when abandoned => NativeBotOutcome.Abandoned,
            // Contact Center counts every bot-only conversation as "deflected". That is the claim the ledger audits,
            // so it is kept even when Copilot Studio's own analytics label the same session differently (in our tests
            // a "thanks, bye" session was Abandoned/UserExit in Copilot Studio and deflected in Contact Center).
            HandlingMode.BotOnly => NativeBotOutcome.Resolved,
            HandlingMode.BotThenHuman => NativeBotOutcome.Escalated,
            _ => NativeBotOutcome.None,
        };

        // Which topic the AI agent handled: from the Copilot Studio transcript, which is written when the bot session
        // ends (typically ~30 min later). Missing now = filled in later by the BotTopicEnricher timer.
        var botSession = botInvolved ? await CopilotSessionAsync(conversationId, start, ct) : null;

        var totalMinutes = Math.Max(0, (end - start).TotalMinutes);
        var handleMinutes = e.GetAttributeValue<int>("msdyn_conversationhandletimeinseconds") / 60.0;
        var botMinutes = mode is HandlingMode.BotOnly or HandlingMode.BotThenHuman ? Math.Max(0, totalMinutes - handleMinutes) : 0;

        var credits = mode is HandlingMode.BotOnly or HandlingMode.BotThenHuman
            ? channel == Channel.Voice ? botMinutes * _options.VoiceCreditsPerBotMinute : _options.DigitalCreditsPerBotConversation
            : 0;

        // The transcript gives the display text and the customer's own words (similarity input, self-declared e-mail).
        var transcript = await TranscriptAsync(conversationId, ct);
        var customerText = transcript?.Raw is { } raw ? TranscriptExtractor.ExtractCustomer(raw) : null;

        // Identity: the linked customer record wins. Otherwise (anonymous chat, typically bot-only) use an e-mail the
        // customer declared in the pre-chat survey or the chat, if it matches exactly one active contact. Self-declared
        // means unverified, so the link is Medium confidence, never High.
        CustomerIdentity identity;
        if (customer is not null)
        {
            identity = new CustomerIdentity(
                ContactId: customer.LogicalName == "contact" ? customer.Id : null,
                AccountId: customer.LogicalName == "account" ? customer.Id : null);
        }
        else if (_options.IdentifyFromSelfDeclaredEmail && await ContactFromSelfDeclaredEmailAsync(conversationId, customerText, ct) is { } contactId)
        {
            identity = CustomerIdentity.FromKnownKey($"contact:{contactId:N}", IdentityConfidence.Medium, contactId);
        }
        else
        {
            identity = CustomerIdentity.Anonymous;
        }

        // The contact's own phone numbers are redacted from stored text (the normaliser redacts the one on the identity).
        var summary = e.GetAttributeValue<string>("msdyn_conversationsummaryfield") ?? transcript?.Text ?? e.GetAttributeValue<string>("msdyn_title");
        var phones = identity.ContactId is { } cid ? await ContactPhonesAsync(cid, ct) : [];
        foreach (var phone in phones.Skip(1))
        {
            summary = InteractionNormalizer.Redact(summary, phone);
            customerText = InteractionNormalizer.Redact(customerText, phone);
        }
        if (phones.Count > 0) identity = identity with { Phone = phones[0] };

        return new Interaction
        {
            Id = Guid.Empty, // normaliser assigns the deterministic ID
            SourceSystem = SourceSystem,
            SourceRecordId = conversationId.ToString("D"),
            Channel = channel,
            HandlingMode = mode,
            NativeBotOutcome = nativeOutcome,
            StartedOn = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)),
            EndedOn = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)),
            Customer = identity,
            IntentCode = e.GetAttributeValue<string>("msdyn_intent"),
            BotTopic = botSession?.Topic,
            IssueText = customerText,
            CaseId = regarding?.LogicalName == "incident" ? regarding.Id : null,
            // Copilot summary when enabled; otherwise the transcript text; the title only as a last resort.
            Summary = summary,
            QueueName = e.GetAttributeValue<EntityReference>("msdyn_cdsqueueid")?.Name,
            HandleMinutes = handleMinutes,
            BotMinutes = botMinutes,
            TelephonyMinutes = channel == Channel.Voice ? totalMinutes : 0,
            AiCredits = credits,
        };
    }

    /// <summary>Label-based channel mapping; robust to option-number differences between versions.</summary>
    public static Channel MapChannel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return Channel.Unknown;
        var l = label.ToLowerInvariant();
        if (l.Contains("voice") || l.Contains("phone") || l.Contains("call")) return Channel.Voice;
        if (l.Contains("sms") || l.Contains("text message")) return Channel.Sms;
        if (l.Contains("email") || l.Contains("e-mail")) return Channel.Email;
        if (l.Contains("chat") && !l.Contains("gpt")) return Channel.Chat;
        if (new[] { "whatsapp", "facebook", "teams", "twitter", "line", "wechat", "apple", "google", "instagram", "social" }.Any(l.Contains))
            return Channel.Social;
        if (l.Contains("entity") || l.Contains("record")) return Channel.Case;
        return Channel.Unknown;
    }

    /// <summary>
    /// Classifies every session participant of the conversation: a system user with msdyn_botapplicationid is a bot
    /// (Microsoft's definition in the conversation metrics documentation), any other user is a human. Returns null when
    /// the query shape is not supported here; the caller then falls back to conversation-level flags and never infers a
    /// bot it cannot see – so the worst case is Unknown, never a false containment claim.
    /// </summary>
    private bool _participantQueryBroken;

    private async Task<(bool Bot, bool Human)?> ParticipantsAsync(Guid conversationId, CancellationToken ct)
    {
        if (_participantQueryBroken) return null;
        var fetch = $"""
            <fetch>
              <entity name="msdyn_sessionparticipant">
                <attribute name="msdyn_agentid" />
                <link-entity name="msdyn_ocsession" from="activityid" to="msdyn_omnichannelsession" alias="s">
                  <filter><condition attribute="msdyn_liveworkitemid" operator="eq" value="{conversationId}" /></filter>
                </link-entity>
                <link-entity name="systemuser" from="systemuserid" to="msdyn_agentid" link-type="outer" alias="u">
                  <attribute name="msdyn_botapplicationid" />
                </link-entity>
              </entity>
            </fetch>
            """;
        try
        {
            var rows = (await service.RetrieveMultipleAsync(new FetchExpression(fetch), ct)).Entities;
            bool bot = false, human = false;
            foreach (var r in rows)
            {
                if (r.GetAttributeValue<EntityReference>("msdyn_agentid") is null) continue;
                var botApp = r.GetAttributeValue<AliasedValue>("u.msdyn_botapplicationid")?.Value as string;
                if (string.IsNullOrWhiteSpace(botApp)) human = true; else bot = true;
            }
            logger.LogInformation("Conversation {Id}: {Count} participant(s), bot={Bot}, human={Human}", conversationId, rows.Count, bot, human);
            return (bot, human);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _participantQueryBroken = true;
            logger.LogWarning(ex, "Participant query not supported in this environment; using conversation-level flags");
            return null;
        }
    }

    private bool _copilotTranscriptBroken;

    /// <summary>
    /// Finds the Copilot Studio transcript of this conversation. Transcripts are keyed by the bot's own conversation,
    /// so candidates are narrowed by start time (±10 min) and confirmed by the Omnichannel conversation id carried in
    /// the transcript's startConversation event – a time match alone is never trusted.
    /// </summary>
    public async Task<CopilotSessionInfo?> CopilotSessionAsync(Guid conversationId, DateTime startUtc, CancellationToken ct = default)
    {
        if (_copilotTranscriptBroken) return null;
        try
        {
            var q = new QueryExpression("conversationtranscript")
            {
                ColumnSet = new ColumnSet("content"),
                TopCount = 25,
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression("conversationstarttime", ConditionOperator.GreaterEqual, startUtc.AddMinutes(-10)),
                        new ConditionExpression("conversationstarttime", ConditionOperator.LessEqual, startUtc.AddMinutes(10)),
                    },
                },
            };
            foreach (var t in (await service.RetrieveMultipleAsync(q, ct)).Entities)
            {
                var content = t.GetAttributeValue<string>("content");
                if (content is null || !content.Contains(conversationId.ToString("D"), StringComparison.OrdinalIgnoreCase)) continue;
                var info = CopilotStudioTranscriptParser.Parse(content);
                if (info?.ConversationId != conversationId) continue;
                logger.LogInformation("Conversation {Id}: Copilot Studio topic '{Topic}' (topics: {Topics}; Copilot outcome {Outcome}/{Reason})",
                    conversationId, info.Topic, string.Join(" > ", info.Topics), info.CopilotOutcome, info.OutcomeReason);
                return info;
            }
            logger.LogInformation("Conversation {Id}: no Copilot Studio transcript yet", conversationId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _copilotTranscriptBroken = true;
            logger.LogWarning(ex, "Copilot Studio transcripts not readable (needs read on conversationtranscript); bot topics will be empty");
            return null;
        }
    }

    private readonly Dictionary<Guid, bool> _botUsers = [];

    private async Task<bool> IsBotUserAsync(Guid userId, CancellationToken ct)
    {
        if (_botUsers.TryGetValue(userId, out var known)) return known;
        try
        {
            var u = await service.RetrieveAsync("systemuser", userId, new ColumnSet("msdyn_botapplicationid"), ct);
            return _botUsers[userId] = !string.IsNullOrWhiteSpace(u.GetAttributeValue<string>("msdyn_botapplicationid"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not classify user {User} as bot or human", userId);
            return _botUsers[userId] = false;
        }
    }

    private sealed record Transcript(string? Text, string Raw);

    private bool _transcriptBroken;

    /// <summary>
    /// Omnichannel persists the conversation transcript as a note (annotation) on an msdyn_transcript record linked to
    /// the conversation. The note body is base64 JSON; its exact shape is not a public contract, so message text is
    /// extracted tolerantly: every "content" string on a non-control message, HTML stripped, capped at 2000 chars.
    /// PII is redacted later by InteractionNormalizer before anything is stored.
    /// </summary>
    private async Task<Transcript?> TranscriptAsync(Guid conversationId, CancellationToken ct)
    {
        var json = await RawTranscriptAsync(conversationId, ct);
        if (json is null) return null;
        var text = TranscriptExtractor.Extract(json);
        logger.LogInformation("Transcript for {Id}: {Chars} chars of message text", conversationId, text?.Length ?? 0);
        return new Transcript(text, json);
    }

    /// <summary>The conversation's Omnichannel transcript document (decoded JSON), or null when not (yet) written.</summary>
    public async Task<string?> RawTranscriptAsync(Guid conversationId, CancellationToken ct = default)
    {
        if (_transcriptBroken) return null;
        try
        {
            var fetch = $"""
                <fetch top="1">
                  <entity name="annotation">
                    <attribute name="documentbody" />
                    <order attribute="createdon" descending="true" />
                    <link-entity name="msdyn_transcript" from="msdyn_transcriptid" to="objectid">
                      <filter><condition attribute="msdyn_liveworkitemidid" operator="eq" value="{conversationId}" /></filter>
                    </link-entity>
                  </entity>
                </fetch>
                """;
            var note = (await service.RetrieveMultipleAsync(new FetchExpression(fetch), ct)).Entities.FirstOrDefault();
            var body = note?.GetAttributeValue<string>("documentbody");
            if (string.IsNullOrEmpty(body))
            {
                logger.LogInformation("No transcript yet for conversation {Id}", conversationId);
                return null;
            }
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _transcriptBroken = true;
            logger.LogWarning(ex, "Transcript could not be read; falling back to title");
            return null;
        }
    }

    /// <summary>The contact's phone numbers (mobile first), used only to redact them from stored text.</summary>
    private async Task<IReadOnlyList<string>> ContactPhonesAsync(Guid contactId, CancellationToken ct)
    {
        try
        {
            var c = await service.RetrieveAsync("contact", contactId, new ColumnSet("mobilephone", "telephone1", "telephone2"), ct);
            return new[] { "mobilephone", "telephone1", "telephone2" }
                .Select(a => c.GetAttributeValue<string>(a))
                .Where(p => !string.IsNullOrWhiteSpace(p) && p.Count(char.IsDigit) >= 7)
                .Distinct().ToList()!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Contact phones not readable for {Contact}", contactId);
            return [];
        }
    }

    private static readonly System.Text.RegularExpressions.Regex EmailPattern =
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Finds an e-mail the customer declared – pre-chat survey answers (conversation context items) first, then the
    /// customer's own chat messages – and returns the contact it identifies, only when exactly one active contact has
    /// that address in emailaddress1.
    /// The address is used for the lookup only; it is never stored or logged.
    /// </summary>
    private async Task<Guid?> ContactFromSelfDeclaredEmailAsync(Guid conversationId, string? customerText, CancellationToken ct)
    {
        var candidates = new List<string>();
        try
        {
            var q = new QueryExpression("msdyn_ocliveworkitemcontextitem") { ColumnSet = new ColumnSet(true), TopCount = 50 };
            q.Criteria.AddCondition("msdyn_ocliveworkitemid", ConditionOperator.Equal, conversationId);
            foreach (var item in (await service.RetrieveMultipleAsync(q, ct)).Entities)
                foreach (var v in item.Attributes.Values.OfType<string>())
                    candidates.AddRange(EmailPattern.Matches(v).Select(m => m.Value));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Context items not readable for {Id}", conversationId);
        }
        // Only what the customer typed: an address written by a representative or the AI agent must not identify them.
        if (customerText is not null)
            candidates.AddRange(EmailPattern.Matches(customerText).Select(m => m.Value));

        foreach (var email in candidates.Select(c => c.Trim().ToLowerInvariant()).Distinct())
        {
            var q = new QueryExpression("contact") { ColumnSet = new ColumnSet(false), TopCount = 2 };
            q.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var matches = (await service.RetrieveMultipleAsync(q, ct)).Entities;
            if (matches.Count == 1)
            {
                logger.LogInformation("Conversation {Id}: customer identified from a self-declared e-mail (Medium confidence)", conversationId);
                return matches[0].Id;
            }
        }
        logger.LogInformation("Conversation {Id}: no linked customer and no uniquely matching self-declared e-mail ({Count} candidate(s))",
            conversationId, candidates.Count);
        return null;
    }

    private async Task<string[]> AvailableColumnsAsync(CancellationToken ct)
    {
        var resp = (RetrieveEntityResponse)await service.ExecuteAsync(new RetrieveEntityRequest
        {
            LogicalName = Table,
            EntityFilters = EntityFilters.Attributes,
        }, ct);
        var available = resp.EntityMetadata.Attributes.Select(a => a.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = Wanted.Where(w => !available.Contains(w)).ToList();
        if (missing.Count > 0) logger.LogWarning("msdyn_ocliveworkitem missing columns in this environment: {Missing}", string.Join(", ", missing));
        return Wanted.Where(available.Contains).ToArray();
    }

    private static DateTime? FirstDate(Entity e, params string[] attrs)
    {
        foreach (var a in attrs)
            if (e.GetAttributeValue<DateTime?>(a) is { } d && d > DateTime.MinValue)
                return d.ToUniversalTime();
        return null;
    }
}
