using Vrl.Core.Model;

namespace Vrl.Core.Policy;

/// <summary>
/// Configurable business rules. Persisted in Dataverse (bpc_windowpolicy) so the business can tune
/// windows and thresholds without a deployment.
/// </summary>
public sealed class LedgerPolicy
{
    /// <summary>Default repeat window when no more specific rule matches.</summary>
    public TimeSpan DefaultWindow { get; init; } = TimeSpan.FromHours(72);

    /// <summary>Score at or above which two interactions are considered the same issue.</summary>
    public double SameIssueThreshold { get; init; } = 0.60;

    /// <summary>Minimum identity confidence required before linking interactions as repeats.</summary>
    public IdentityConfidence MinimumLinkConfidence { get; init; } = IdentityConfidence.Medium;

    /// <summary>
    /// When true, a bot-only session the customer abandoned and never returned from is classified
    /// <see cref="OutcomeVerdict.Unknown"/> rather than VerifiedResolved. This is deliberately conservative:
    /// silence after abandonment is not evidence of resolution.
    /// </summary>
    public bool TreatSilentAbandonmentAsUnknown { get; init; } = true;

    public IReadOnlyDictionary<Channel, TimeSpan> ChannelWindows { get; init; } =
        new Dictionary<Channel, TimeSpan> { [Channel.Case] = TimeSpan.FromDays(7) };

    /// <summary>Keyed by intent family (case-insensitive). Takes precedence over channel windows.</summary>
    public IReadOnlyDictionary<string, TimeSpan> IntentFamilyWindows { get; init; } =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

    public SimilarityWeights Weights { get; init; } = new();

    /// <summary>Longest window in force; bounds how far back the engine needs to load history.</summary>
    public TimeSpan MaxWindow =>
        new[] { DefaultWindow }
            .Concat(ChannelWindows.Values)
            .Concat(IntentFamilyWindows.Values)
            .Max();

    public TimeSpan WindowFor(Interaction interaction)
    {
        if (interaction.IntentFamily is { } fam && IntentFamilyWindows.TryGetValue(fam, out var w1)) return w1;
        if (ChannelWindows.TryGetValue(interaction.Channel, out var w2)) return w2;
        return DefaultWindow;
    }

    public static LedgerPolicy Default { get; } = new();
}

/// <summary>Weights for the explainable same-issue score. All contributions are additive and clamped to [0,1].</summary>
public sealed record SimilarityWeights
{
    public double SameCase { get; init; } = 0.70;
    public double IntentExact { get; init; } = 0.60;
    public double IntentFamily { get; init; } = 0.30;
    /// <summary>Negative evidence: both interactions carry intents from different families.</summary>
    public double IntentConflictPenalty { get; init; } = -0.30;
    /// <summary>A shared bare number (7–12 digits): could be an order, an account or coincidence.</summary>
    public double SharedReference { get; init; } = 0.35;
    /// <summary>
    /// A shared structured reference (ORD-4455667, TRK-55667788, a VIN). Strong, but deliberately below the threshold
    /// on its own: the same order can carry different issues (refund vs. delivery), so it needs corroboration from
    /// text similarity or intent.
    /// </summary>
    public double SharedStructuredReference { get; init; } = 0.50;
    /// <summary>Maximum contribution of semantic similarity; scaled linearly above <see cref="SemanticFloor"/>.</summary>
    public double Semantic { get; init; } = 0.65;
    /// <remarks>
    /// Cosine distributions are model-specific, so the floor must be calibrated per embedding model.
    /// Use <see cref="ForEmbeddingModel"/> rather than hard-coding.
    /// </remarks>
    public double SemanticFloor { get; init; } = 0.55;

    /// <summary>
    /// Starting calibrations. Validate against a labelled sample from your own tenant (see docs/Calibration.md) –
    /// these are engineering defaults, not measured truths.
    /// </summary>
    public static SimilarityWeights ForEmbeddingModel(string? modelId) => modelId switch
    {
        // Feature hashing: unrelated text ≈ 0.0–0.25, close paraphrase ≈ 0.6–0.8.
        not null when modelId.StartsWith("local-hash", StringComparison.OrdinalIgnoreCase) =>
            new SimilarityWeights { SemanticFloor = 0.30 },
        // text-embedding-3-*: unrelated support texts typically sit well below 0.5.
        not null when modelId.Contains("embedding-3", StringComparison.OrdinalIgnoreCase) =>
            new SimilarityWeights { SemanticFloor = 0.50 },
        // text-embedding-ada-002 has a high baseline similarity for any two texts.
        not null when modelId.Contains("ada-002", StringComparison.OrdinalIgnoreCase) =>
            new SimilarityWeights { SemanticFloor = 0.80 },
        _ => new SimilarityWeights(),
    };
}
