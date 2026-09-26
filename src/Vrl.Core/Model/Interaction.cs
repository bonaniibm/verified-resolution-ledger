namespace Vrl.Core.Model;

/// <summary>
/// Canonical, source-agnostic representation of one customer contact.
/// Adapters (Omnichannel, Copilot Studio, Case, embedded 3P CRM, synthetic) all normalise into this shape,
/// so the engine never depends on a specific source schema.
/// </summary>
public sealed record Interaction
{
    public required Guid Id { get; init; }

    /// <summary>e.g. "omnichannel", "copilotstudio", "case", "salesforce", "synthetic".</summary>
    public required string SourceSystem { get; init; }

    /// <summary>Primary key in the source system. (SourceSystem, SourceRecordId) is the idempotency key.</summary>
    public required string SourceRecordId { get; init; }

    public Channel Channel { get; init; } = Channel.Unknown;
    public HandlingMode HandlingMode { get; init; } = HandlingMode.Unknown;
    public NativeBotOutcome NativeBotOutcome { get; init; } = NativeBotOutcome.None;

    public required DateTimeOffset StartedOn { get; init; }
    public required DateTimeOffset EndedOn { get; init; }

    public CustomerIdentity Customer { get; init; } = CustomerIdentity.Anonymous;

    /// <summary>Specific intent (e.g. "billing.refund.status").</summary>
    public string? IntentCode { get; init; }

    /// <summary>Intent family / parent (e.g. "billing"). Used for partial matches and window policy.</summary>
    public string? IntentFamily { get; init; }

    public string? DispositionCode { get; init; }
    public Guid? CaseId { get; init; }

    /// <summary>
    /// Copilot summary or the conversation transcript: shown to representatives and supervisors, and used for reference
    /// extraction. Also the semantic-similarity input when <see cref="IssueText"/> is absent.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// The customer's own words only (no bot or representative replies, no system messages). Preferred input for
    /// semantic similarity: boilerplate such as "Hello, I'm … How can I help?" dilutes it. Not persisted.
    /// </summary>
    public string? IssueText { get; init; }

    public string? QueueName { get; init; }
    public string? BotTopic { get; init; }
    public string? KnowledgeArticleId { get; init; }

    /// <summary>True when the representative deliberately scheduled a callback / follow-up.</summary>
    public bool FollowUpScheduled { get; init; }

    public double BotMinutes { get; init; }
    public double HandleMinutes { get; init; }
    public double TelephonyMinutes { get; init; }
    public double AiCredits { get; init; }

    /// <summary>Optional pre-computed embedding of <see cref="Summary"/>.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Model that produced <see cref="Embedding"/>. Vectors from different models are never compared.</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>
    /// Episode key already persisted for this interaction, if any. Lets the engine keep episode identity stable when the
    /// root of a long episode has aged out of the loaded look-back horizon.
    /// </summary>
    public string? ExistingEpisodeKey { get; init; }

    /// <summary>What the store currently holds for this interaction. Lets repositories write only rows that changed.</summary>
    public LedgerState? Persisted { get; init; }

    public bool IsBotOnly => HandlingMode == HandlingMode.BotOnly;

    public TimeSpan Duration => EndedOn - StartedOn;
}

/// <summary>Snapshot of the persisted evaluation of an interaction (null when never evaluated).</summary>
public sealed record LedgerState(
    OutcomeVerdict Verdict,
    bool IsFinal,
    string? EpisodeKey,
    Guid? PredecessorId,
    Guid? SuccessorId,
    decimal CostTotal,
    decimal? PredecessorScore = null,
    string? ReasonJson = null);
