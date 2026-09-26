using System.Collections.Concurrent;
using Vrl.Core.Abstractions;
using Vrl.Core.Engine;
using Vrl.Core.Model;

namespace Vrl.Core.Processing;

/// <summary>Thread-safe in-memory store for tests, local demos and the synthetic simulator.</summary>
public sealed class InMemoryLedgerRepository : ILedgerRepository
{
    private readonly ConcurrentDictionary<Guid, Interaction> _interactions = new();
    private readonly ConcurrentDictionary<Guid, InteractionEvaluation> _evaluations = new();
    private readonly ConcurrentDictionary<string, Episode> _episodes = new();

    public IReadOnlyCollection<Interaction> Interactions => _interactions.Values.ToList();
    public IReadOnlyCollection<InteractionEvaluation> Evaluations => _evaluations.Values.ToList();
    public IReadOnlyCollection<Episode> Episodes => _episodes.Values.ToList();

    public int WriteCount { get; private set; }

    public Task UpsertInteractionAsync(Interaction interaction, CancellationToken cancellationToken = default)
    {
        _interactions[interaction.Id] = interaction;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Interaction>> LoadCustomerHistoryAsync(
        string customerKey, DateTimeOffset since, int maxItems, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Interaction> list = _interactions.Values
            .Where(i => i.Customer.ResolveKey().Key == customerKey)
            .Where(i => i.EndedOn >= since || (_evaluations.TryGetValue(i.Id, out var e) && !e.IsFinal))
            .Select(i => _evaluations.TryGetValue(i.Id, out var e) ? i with { ExistingEpisodeKey = e.EpisodeKey } : i)
            .OrderBy(i => i.StartedOn)
            .Take(maxItems + 1)
            .ToList();
        return Task.FromResult(list);
    }

    public Task SaveEvaluationAsync(
        string? customerKey, IReadOnlyList<Interaction> scope, EvaluationResult result, DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        foreach (var e in result.Interactions)
        {
            if (_evaluations.TryGetValue(e.InteractionId, out var old) && Same(old, e)) continue;
            _evaluations[e.InteractionId] = e;
            WriteCount++;
        }

        if (customerKey is not null)
        {
            var keep = result.Episodes.Select(e => e.EpisodeKey).ToHashSet();
            foreach (var stale in _episodes.Values.Where(e => e.CustomerKey == customerKey && e.LastEndedOn >= since && !keep.Contains(e.EpisodeKey)).ToList())
                _episodes.TryRemove(stale.EpisodeKey, out _);
        }

        foreach (var ep in result.Episodes) _episodes[ep.EpisodeKey] = ep;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> FindCustomerKeysDueForMaturationAsync(
        DateTimeOffset asOf, int maxKeys, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> keys = _evaluations.Values
            .Where(e => !e.IsFinal && e.Reason.WindowClosesOn <= asOf && e.CustomerKey is not null)
            .Select(e => e.CustomerKey!)
            .Distinct()
            .Take(maxKeys)
            .ToList();
        return Task.FromResult(keys);
    }

    private static bool Same(InteractionEvaluation a, InteractionEvaluation b) =>
        a.Verdict == b.Verdict && a.IsFinal == b.IsFinal && a.EpisodeKey == b.EpisodeKey &&
        a.PredecessorId == b.PredecessorId && a.SuccessorId == b.SuccessorId;
}
