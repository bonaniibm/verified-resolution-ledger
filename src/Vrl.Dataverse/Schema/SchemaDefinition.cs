using Vrl.Core.Model;
using S = Vrl.Dataverse.Schema.LedgerSchema;

namespace Vrl.Dataverse.Schema;

public enum ColumnKind { Text, Memo, Integer, Decimal, Boolean, DateTime, LocalChoice, GlobalChoice, Lookup }

public sealed record ColumnDef(
    string LogicalName,
    string DisplayName,
    ColumnKind Kind,
    string Description,
    int MaxLength = 200,
    int Precision = 2,
    Type? ChoiceEnum = null,
    string? GlobalChoiceName = null,
    string? LookupTarget = null,
    string? RelationshipName = null);

public sealed record TableDef(
    string LogicalName,
    string DisplayName,
    string PluralName,
    string Description,
    string PrimaryNameColumn,
    IReadOnlyList<ColumnDef> Columns,
    IReadOnlyList<(string SchemaName, string DisplayName, string[] Columns)> AlternateKeys);

/// <summary>Declarative definition of the ledger's Dataverse schema, consumed by <see cref="SchemaProvisioner"/>.</summary>
public static class SchemaDefinition
{
    public static IReadOnlyList<(string Name, string DisplayName, Type Enum)> GlobalChoices { get; } =
    [
        (S.GlobalChannelChoice, "Ledger Channel", typeof(Channel)),
    ];

    public static IReadOnlyList<TableDef> Tables { get; } =
    [
        new(S.Interaction.Table, "Ledger Interaction", "Ledger Interactions",
            "Canonical, source-agnostic customer contact with its verified outcome.",
            S.Interaction.Name,
            [
                new(S.Interaction.SourceSystem, "Source System", ColumnKind.Text, "omnichannel, copilotstudio, case, salesforce, synthetic…", 50),
                new(S.Interaction.SourceRecordId, "Source Record Id", ColumnKind.Text, "Primary key in the source system.", 100),
                new(S.Interaction.Channel, "Channel", ColumnKind.GlobalChoice, "Normalised channel.", GlobalChoiceName: S.GlobalChannelChoice),
                new(S.Interaction.HandlingMode, "Handling Mode", ColumnKind.LocalChoice, "Bot only, bot then human, human only, case only.", ChoiceEnum: typeof(HandlingMode)),
                new(S.Interaction.NativeBotOutcome, "Native Bot Outcome", ColumnKind.LocalChoice, "What the source platform reported.", ChoiceEnum: typeof(NativeBotOutcome)),
                new(S.Interaction.StartedOn, "Started On", ColumnKind.DateTime, "Interaction start (UTC)."),
                new(S.Interaction.EndedOn, "Ended On", ColumnKind.DateTime, "Interaction end (UTC)."),
                new(S.Interaction.ContactId, "Contact", ColumnKind.Lookup, "Identified contact, when known.", LookupTarget: "contact", RelationshipName: "bpc_contact_bpc_interaction"),
                new(S.Interaction.AccountId, "Account", ColumnKind.Lookup, "Identified account, when known.", LookupTarget: "account", RelationshipName: "bpc_account_bpc_interaction"),
                new(S.Interaction.CustomerKey, "Customer Key", ColumnKind.Text, "Pseudonymous partition key (hashed for phone/e-mail).", 100),
                new(S.Interaction.IdentityConfidence, "Identity Confidence", ColumnKind.LocalChoice, "How reliable the customer key is.", ChoiceEnum: typeof(IdentityConfidence)),
                new(S.Interaction.IntentCode, "Intent", ColumnKind.Text, "Specific intent code.", 200),
                new(S.Interaction.IntentFamily, "Intent Family", ColumnKind.Text, "Parent intent / category.", 100),
                new(S.Interaction.DispositionCode, "Disposition Code", ColumnKind.Text, "Wrap-up / disposition code.", 100),
                new(S.Interaction.CaseId, "Case", ColumnKind.Lookup, "Linked case.", LookupTarget: "incident", RelationshipName: "bpc_incident_bpc_interaction"),
                new(S.Interaction.Summary, "Summary", ColumnKind.Memo, "Redacted conversation text or Copilot summary (display, reference matching).", 4000),
                new(S.Interaction.QueueName, "Queue", ColumnKind.Text, "Queue name at close.", 200),
                new(S.Interaction.BotTopic, "Bot Topic", ColumnKind.Text, "Last business topic the Copilot Studio agent recognised.", 200),
                new(S.Interaction.KnowledgeArticleId, "Knowledge Article", ColumnKind.Text, "Knowledge article used, if any.", 100),
                new(S.Interaction.FollowUpScheduled, "Follow-up Scheduled", ColumnKind.Boolean, "Representative deliberately scheduled a follow-up."),
                new(S.Interaction.BotMinutes, "Bot Minutes", ColumnKind.Decimal, "Minutes with the AI agent.", Precision: 2),
                new(S.Interaction.HandleMinutes, "Handle Minutes", ColumnKind.Decimal, "Human handle time in minutes.", Precision: 2),
                new(S.Interaction.TelephonyMinutes, "Telephony Minutes", ColumnKind.Decimal, "Billable telephony minutes.", Precision: 2),
                new(S.Interaction.AiCredits, "AI Credits", ColumnKind.Decimal, "Copilot credits consumed.", Precision: 2),
                new(S.Interaction.Embedding, "Embedding", ColumnKind.Memo, "Base64 float32 embedding of the customer's words (or the summary).", 65536),
                new(S.Interaction.EmbeddingModel, "Embedding Model", ColumnKind.Text, "Model that produced the embedding.", 100),
                new(S.Interaction.EpisodeId, "Episode", ColumnKind.Lookup, "Contact episode.", LookupTarget: S.Episode.Table, RelationshipName: "bpc_contactepisode_bpc_interaction"),
                new(S.Interaction.EpisodeKey, "Episode Key", ColumnKind.Text, "Deterministic episode key.", 100),
                new(S.Interaction.PredecessorId, "Repeat Of", ColumnKind.Lookup, "Earlier interaction this one repeats.", LookupTarget: S.Interaction.Table, RelationshipName: "bpc_interaction_predecessor"),
                new(S.Interaction.SuccessorId, "Repeated By", ColumnKind.Lookup, "Later interaction that repeated this one.", LookupTarget: S.Interaction.Table, RelationshipName: "bpc_interaction_successor"),
                new(S.Interaction.PredecessorScore, "Same-issue Score", ColumnKind.Decimal, "Score of the link to the predecessor.", Precision: 4),
                new(S.Interaction.Verdict, "Verdict", ColumnKind.LocalChoice, "Verified outcome.", ChoiceEnum: typeof(OutcomeVerdict)),
                new(S.Interaction.VerdictFinal, "Verdict Final", ColumnKind.Boolean, "True once the verdict can no longer change."),
                new(S.Interaction.VerdictCode, "Verdict Code", ColumnKind.Text, "Machine-readable reason.", 50),
                new(S.Interaction.VerdictReason, "Verdict Reason", ColumnKind.Memo, "JSON explanation with contributing signals.", 8000),
                new(S.Interaction.WindowClosesOn, "Window Closes On", ColumnKind.DateTime, "When the repeat window closes."),
                new(S.Interaction.EvaluatedOn, "Evaluated On", ColumnKind.DateTime, "Last evaluation time."),
                new(S.Interaction.EngineVersion, "Engine Version", ColumnKind.Text, "Engine version that produced the verdict.", 20),
                new(S.Interaction.CostAi, "Cost – AI", ColumnKind.Decimal, "AI credit cost.", Precision: 4),
                new(S.Interaction.CostTelephony, "Cost – Telephony", ColumnKind.Decimal, "Telephony cost.", Precision: 4),
                new(S.Interaction.CostLabor, "Cost – Labor", ColumnKind.Decimal, "Loaded labor cost.", Precision: 4),
                new(S.Interaction.CostTotal, "Cost – Total", ColumnKind.Decimal, "Total cost.", Precision: 4),
            ],
            [("bpc_sourcekey", "Source Key", [S.Interaction.SourceSystem, S.Interaction.SourceRecordId])]),

        new(S.Episode.Table, "Contact Episode", "Contact Episodes",
            "All same-issue contacts from one customer, stitched across channels.",
            S.Episode.Name,
            [
                new(S.Episode.EpisodeKey, "Episode Key", ColumnKind.Text, "Deterministic key.", 100),
                new(S.Episode.CustomerKey, "Customer Key", ColumnKind.Text, "Pseudonymous customer key.", 100),
                new(S.Episode.ContactId, "Contact", ColumnKind.Lookup, "Identified contact.", LookupTarget: "contact", RelationshipName: "bpc_contact_bpc_contactepisode"),
                new(S.Episode.Status, "Status", ColumnKind.LocalChoice, "Open, resolved, unresolved, unknown.", ChoiceEnum: typeof(EpisodeStatus)),
                new(S.Episode.ContactCount, "Contact Count", ColumnKind.Integer, "Number of contacts in the episode."),
                new(S.Episode.FirstStartedOn, "First Contact", ColumnKind.DateTime, "Start of first contact."),
                new(S.Episode.LastEndedOn, "Last Contact", ColumnKind.DateTime, "End of last contact."),
                new(S.Episode.TotalCost, "Total Cost", ColumnKind.Decimal, "Sum of member interaction costs.", Precision: 4),
                new(S.Episode.RootCauseIntent, "Root-cause Intent", ColumnKind.Text, "Intent of the first failed contact.", 200),
                new(S.Episode.RootCauseTopic, "Root-cause Bot Topic", ColumnKind.Text, "Bot topic of the first failed contact.", 200),
                new(S.Episode.RootCauseQueue, "Root-cause Queue", ColumnKind.Text, "Queue of the first failed contact.", 200),
                new(S.Episode.Channels, "Channels", ColumnKind.Text, "Channels involved.", 200),
                new(S.Episode.EngineVersion, "Engine Version", ColumnKind.Text, "Engine version.", 20),
            ],
            [(S.Episode.EpisodeKeyAltKey, "Episode Key", [S.Episode.EpisodeKey])]),

        new(S.WindowPolicy.Table, "Repeat Window Policy", "Repeat Window Policies",
            "Business-owned repeat windows and thresholds.",
            S.WindowPolicy.Name,
            [
                new(S.WindowPolicy.Scope, "Scope", ColumnKind.LocalChoice, "Global, channel or intent family.", ChoiceEnum: typeof(LedgerSchema.PolicyScope)),
                new(S.WindowPolicy.Channel, "Channel", ColumnKind.GlobalChoice, "Channel for channel-scoped rules.", GlobalChoiceName: S.GlobalChannelChoice),
                new(S.WindowPolicy.IntentFamily, "Intent Family", ColumnKind.Text, "Intent family for intent-scoped rules.", 100),
                new(S.WindowPolicy.WindowHours, "Window (hours)", ColumnKind.Integer, "Repeat window in hours."),
                new(S.WindowPolicy.SameIssueThreshold, "Same-issue Threshold", ColumnKind.Decimal, "Global rule only: score threshold (0–1).", Precision: 2),
            ],
            []),

        new(S.CostRate.Table, "Cost Rate", "Cost Rates",
            "Finance-owned rate card for cost per resolved outcome.",
            S.CostRate.Name,
            [
                new(S.CostRate.RateType, "Rate Type", ColumnKind.LocalChoice, "Credit price, telephony/min, labor/hour, channel/min.", ChoiceEnum: typeof(LedgerSchema.RateType)),
                new(S.CostRate.Channel, "Channel", ColumnKind.GlobalChoice, "Channel for channel rates.", GlobalChoiceName: S.GlobalChannelChoice),
                new(S.CostRate.QueueName, "Queue", ColumnKind.Text, "Queue for queue-specific labor rates.", 200),
                new(S.CostRate.Amount, "Amount", ColumnKind.Decimal, "Rate amount.", Precision: 4),
                new(S.CostRate.Currency, "Currency Code", ColumnKind.Text, "ISO currency code, e.g. EUR.", 3),
            ],
            []),
    ];
}
