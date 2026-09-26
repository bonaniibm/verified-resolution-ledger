using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xrm.Sdk;
using Vrl.Core.Engine;
using Vrl.Core.Model;
using Vrl.Core.Similarity;
using Vrl.Core.Util;
using I = Vrl.Dataverse.Schema.LedgerSchema.Interaction;
using E = Vrl.Dataverse.Schema.LedgerSchema.Episode;

namespace Vrl.Dataverse;

/// <summary>Maps between the canonical model and Dataverse entities.</summary>
public static class InteractionMapper
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly string[] SourceColumns =
    [
        I.SourceSystem, I.SourceRecordId, I.Channel, I.HandlingMode, I.NativeBotOutcome, I.StartedOn, I.EndedOn,
        I.ContactId, I.AccountId, I.CustomerKey, I.IdentityConfidence, I.IntentCode, I.IntentFamily, I.DispositionCode,
        I.CaseId, I.Summary, I.QueueName, I.BotTopic, I.KnowledgeArticleId, I.FollowUpScheduled, I.BotMinutes,
        I.HandleMinutes, I.TelephonyMinutes, I.AiCredits, I.Embedding, I.EmbeddingModel,
    ];

    public static readonly string[] StateColumns =
    [
        I.Verdict, I.VerdictFinal, I.EpisodeKey, I.PredecessorId, I.SuccessorId, I.CostTotal, I.PredecessorScore, I.VerdictReason,
    ];

    /// <summary>Source-side fields only. Evaluation fields are written separately so ingestion never clobbers verdicts.</summary>
    public static Entity ToSourceEntity(Interaction i, string? embeddingModel)
    {
        var (key, confidence) = i.Customer.ResolveKey();
        var e = new Entity(I.Table, i.Id)
        {
            [I.Name] = Truncate($"{i.Channel} · {i.StartedOn.UtcDateTime:yyyy-MM-dd HH:mm} · {i.IntentCode ?? "no intent"}", 200),
            [I.SourceSystem] = i.SourceSystem,
            [I.SourceRecordId] = i.SourceRecordId,
            [I.Channel] = new OptionSetValue((int)i.Channel),
            [I.HandlingMode] = new OptionSetValue((int)i.HandlingMode),
            [I.NativeBotOutcome] = new OptionSetValue((int)i.NativeBotOutcome),
            [I.StartedOn] = i.StartedOn.UtcDateTime,
            [I.EndedOn] = i.EndedOn.UtcDateTime,
            [I.ContactId] = i.Customer.ContactId is { } c ? new EntityReference("contact", c) : null,
            [I.AccountId] = i.Customer.AccountId is { } a ? new EntityReference("account", a) : null,
            [I.CustomerKey] = key,
            [I.IdentityConfidence] = new OptionSetValue((int)confidence),
            [I.IntentCode] = Truncate(i.IntentCode, 200),
            [I.IntentFamily] = Truncate(i.IntentFamily, 100),
            [I.DispositionCode] = Truncate(i.DispositionCode, 100),
            [I.CaseId] = i.CaseId is { } cs ? new EntityReference("incident", cs) : null,
            [I.Summary] = Truncate(i.Summary, 4000),
            [I.QueueName] = Truncate(i.QueueName, 200),
            [I.BotTopic] = Truncate(i.BotTopic, 200),
            [I.KnowledgeArticleId] = Truncate(i.KnowledgeArticleId, 100),
            [I.FollowUpScheduled] = i.FollowUpScheduled,
            [I.BotMinutes] = Dec(i.BotMinutes),
            [I.HandleMinutes] = Dec(i.HandleMinutes),
            [I.TelephonyMinutes] = Dec(i.TelephonyMinutes),
            [I.AiCredits] = Dec(i.AiCredits),
        };

        if (i.Embedding is { Length: > 0 } emb)
        {
            e[I.Embedding] = VectorMath.ToBase64(emb);
            e[I.EmbeddingModel] = i.EmbeddingModel ?? embeddingModel;
        }

        return e;
    }

    public static Interaction FromEntity(Entity e)
    {
        var contact = e.GetAttributeValue<EntityReference>(I.ContactId)?.Id;
        var account = e.GetAttributeValue<EntityReference>(I.AccountId)?.Id;
        var confidence = (IdentityConfidence)(e.GetAttributeValue<OptionSetValue>(I.IdentityConfidence)?.Value ?? (int)IdentityConfidence.None);

        var verdict = e.GetAttributeValue<OptionSetValue>(I.Verdict);
        var episodeKey = e.GetAttributeValue<string>(I.EpisodeKey);

        return new Interaction
        {
            Id = e.Id,
            SourceSystem = e.GetAttributeValue<string>(I.SourceSystem) ?? "unknown",
            SourceRecordId = e.GetAttributeValue<string>(I.SourceRecordId) ?? e.Id.ToString(),
            Channel = Enum<Channel>(e, I.Channel, Channel.Unknown),
            HandlingMode = Enum<HandlingMode>(e, I.HandlingMode, HandlingMode.Unknown),
            NativeBotOutcome = Enum<NativeBotOutcome>(e, I.NativeBotOutcome, NativeBotOutcome.None),
            StartedOn = Utc(e.GetAttributeValue<DateTime?>(I.StartedOn)),
            EndedOn = Utc(e.GetAttributeValue<DateTime?>(I.EndedOn)),
            Customer = CustomerIdentity.FromKnownKey(e.GetAttributeValue<string>(I.CustomerKey), confidence, contact, account),
            IntentCode = e.GetAttributeValue<string>(I.IntentCode),
            IntentFamily = e.GetAttributeValue<string>(I.IntentFamily),
            DispositionCode = e.GetAttributeValue<string>(I.DispositionCode),
            CaseId = e.GetAttributeValue<EntityReference>(I.CaseId)?.Id,
            Summary = e.GetAttributeValue<string>(I.Summary),
            QueueName = e.GetAttributeValue<string>(I.QueueName),
            BotTopic = e.GetAttributeValue<string>(I.BotTopic),
            KnowledgeArticleId = e.GetAttributeValue<string>(I.KnowledgeArticleId),
            FollowUpScheduled = e.GetAttributeValue<bool>(I.FollowUpScheduled),
            BotMinutes = (double)e.GetAttributeValue<decimal>(I.BotMinutes),
            HandleMinutes = (double)e.GetAttributeValue<decimal>(I.HandleMinutes),
            TelephonyMinutes = (double)e.GetAttributeValue<decimal>(I.TelephonyMinutes),
            AiCredits = (double)e.GetAttributeValue<decimal>(I.AiCredits),
            Embedding = VectorMath.FromBase64(e.GetAttributeValue<string>(I.Embedding)),
            EmbeddingModel = e.GetAttributeValue<string>(I.EmbeddingModel),
            ExistingEpisodeKey = episodeKey,
            Persisted = verdict is null
                ? null
                : new LedgerState(
                    (OutcomeVerdict)verdict.Value,
                    e.GetAttributeValue<bool>(I.VerdictFinal),
                    episodeKey,
                    e.GetAttributeValue<EntityReference>(I.PredecessorId)?.Id,
                    e.GetAttributeValue<EntityReference>(I.SuccessorId)?.Id,
                    e.GetAttributeValue<decimal>(I.CostTotal),
                    e.GetAttributeValue<decimal?>(I.PredecessorScore),
                    e.GetAttributeValue<string>(I.VerdictReason)),
        };
    }

    public static bool IsUnchanged(LedgerState? persisted, InteractionEvaluation ev) =>
        persisted is not null
        && persisted.Verdict == ev.Verdict
        && persisted.IsFinal == ev.IsFinal
        && persisted.EpisodeKey == ev.EpisodeKey
        && persisted.PredecessorId == ev.PredecessorId
        && persisted.SuccessorId == ev.SuccessorId
        && persisted.CostTotal == ev.Cost.Total
        // Scores and the explanation change when scoring is recalibrated even if the verdict does not; the stored
        // explanation must never go stale, because agents and auditors read it.
        && persisted.PredecessorScore == (ev.PredecessorScore is { } sc ? Math.Round((decimal)sc, 4) : null)
        && persisted.ReasonJson == ReasonJson(ev);

    private static string ReasonJson(InteractionEvaluation ev) => Truncate(JsonSerializer.Serialize(ev.Reason, Json), 8000)!;

    public static Entity ToEvaluationUpdate(InteractionEvaluation ev, DateTimeOffset evaluatedOn, string engineVersion) =>
        new(I.Table, ev.InteractionId)
        {
            [I.Verdict] = new OptionSetValue((int)ev.Verdict),
            [I.VerdictFinal] = ev.IsFinal,
            [I.VerdictCode] = ev.Reason.Code,
            [I.VerdictReason] = ReasonJson(ev),
            [I.WindowClosesOn] = ev.Reason.WindowClosesOn.UtcDateTime,
            [I.EvaluatedOn] = evaluatedOn.UtcDateTime,
            [I.EngineVersion] = engineVersion,
            [I.EpisodeKey] = ev.EpisodeKey,
            [I.EpisodeId] = new EntityReference(E.Table, DeterministicGuid.ForEpisode(ev.EpisodeKey)),
            [I.PredecessorId] = ev.PredecessorId is { } p ? new EntityReference(I.Table, p) : null,
            [I.SuccessorId] = ev.SuccessorId is { } s ? new EntityReference(I.Table, s) : null,
            [I.PredecessorScore] = ev.PredecessorScore is { } sc ? Math.Round((decimal)sc, 4) : null,
            [I.CostAi] = ev.Cost.Ai,
            [I.CostTelephony] = ev.Cost.Telephony,
            [I.CostLabor] = ev.Cost.Labor,
            [I.CostTotal] = ev.Cost.Total,
        };

    public static Entity ToEpisodeEntity(Episode ep, Guid? contactId, string engineVersion) =>
        new(E.Table, DeterministicGuid.ForEpisode(ep.EpisodeKey))
        {
            [E.Name] = Truncate($"{ep.RootCauseIntent ?? "Episode"} · {ep.ContactCount} contact(s) · {ep.FirstStartedOn.UtcDateTime:yyyy-MM-dd}", 200),
            [E.EpisodeKey] = ep.EpisodeKey,
            [E.CustomerKey] = ep.CustomerKey,
            [E.ContactId] = contactId is { } c ? new EntityReference("contact", c) : null,
            [E.Status] = new OptionSetValue((int)ep.Status),
            [E.ContactCount] = ep.ContactCount,
            [E.FirstStartedOn] = ep.FirstStartedOn.UtcDateTime,
            [E.LastEndedOn] = ep.LastEndedOn.UtcDateTime,
            [E.TotalCost] = ep.TotalCost,
            [E.RootCauseIntent] = Truncate(ep.RootCauseIntent, 200),
            [E.RootCauseTopic] = Truncate(ep.RootCauseTopic, 200),
            [E.RootCauseQueue] = Truncate(ep.RootCauseQueue, 200),
            [E.Channels] = Truncate(string.Join(", ", ep.Channels), 200),
            [E.EngineVersion] = engineVersion,
        };

    private static T Enum<T>(Entity e, string attr, T fallback) where T : struct, System.Enum
    {
        var v = e.GetAttributeValue<OptionSetValue>(attr)?.Value;
        return v is { } x && System.Enum.IsDefined(typeof(T), x) ? (T)(object)x : fallback;
    }

    private static DateTimeOffset Utc(DateTime? dt) =>
        dt is null ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(dt.Value.ToUniversalTime(), DateTimeKind.Utc));

    private static decimal Dec(double d) => Math.Round((decimal)d, 2);

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
