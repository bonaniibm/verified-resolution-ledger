using Vrl.Core.Costing;
using Vrl.Core.Engine;
using Vrl.Core.Model;
using Vrl.Core.Policy;

namespace Vrl.Core.Abstractions;

/// <summary>Persistence port. Implemented by Dataverse (production) and in-memory (tests, demo).</summary>
public interface ILedgerRepository
{
    /// <summary>Idempotent create-or-update of the canonical interaction, keyed by (SourceSystem, SourceRecordId).</summary>
    Task UpsertInteractionAsync(Interaction interaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// All interactions for a customer that ended on/after <paramref name="since"/>, plus any older ones whose verdict
    /// is not final. Returns at most <paramref name="maxItems"/> + 1 rows so callers can detect over-broad identities.
    /// </summary>
    Task<IReadOnlyList<Interaction>> LoadCustomerHistoryAsync(
        string customerKey, DateTimeOffset since, int maxItems, CancellationToken cancellationToken = default);

    /// <summary>
    /// All interactions of a customer that carry one of <paramref name="episodeKeys"/>. The processor uses this so an
    /// evaluation always sees complete episodes: otherwise an open contact whose (final) predecessors fall outside
    /// the history horizon would be re-evaluated alone, losing its link and truncating the stored episode.
    /// </summary>
    Task<IReadOnlyList<Interaction>> LoadEpisodeMembersAsync(
        string customerKey, IReadOnlyCollection<string> episodeKeys, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists verdicts and episodes for the evaluated scope. Implementations should write only changed rows and
    /// remove episodes (ending on/after <paramref name="since"/>) that no longer exist in <paramref name="result"/>.
    /// </summary>
    Task SaveEvaluationAsync(
        string? customerKey,
        IReadOnlyList<Interaction> scope,
        EvaluationResult result,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>Distinct customer keys that have non-final verdicts whose repeat window has closed.</summary>
    Task<IReadOnlyList<string>> FindCustomerKeysDueForMaturationAsync(
        DateTimeOffset asOf, int maxKeys, CancellationToken cancellationToken = default);
}

public interface ILedgerConfigurationProvider
{
    Task<(LedgerPolicy Policy, CostRateCard Rates)> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class StaticLedgerConfigurationProvider(LedgerPolicy policy, CostRateCard rates) : ILedgerConfigurationProvider
{
    public Task<(LedgerPolicy Policy, CostRateCard Rates)> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((policy, rates));
}
