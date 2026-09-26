using Vrl.Core.Costing;
using Vrl.Core.Model;
using Vrl.Core.Policy;
using Vrl.Core.Similarity;

namespace Vrl.Core.Engine;

public sealed record VerdictReason(
    string Code,
    string Message,
    DateTimeOffset WindowClosesOn,
    IReadOnlyList<SignalContribution> LinkSignals);

public sealed record InteractionEvaluation(
    Guid InteractionId,
    string? CustomerKey,
    IdentityConfidence IdentityConfidence,
    OutcomeVerdict Verdict,
    bool IsFinal,
    Guid? PredecessorId,
    double? PredecessorScore,
    Guid? SuccessorId,
    string EpisodeKey,
    InteractionCost Cost,
    VerdictReason Reason);

public sealed record Episode(
    string EpisodeKey,
    string? CustomerKey,
    IReadOnlyList<Guid> InteractionIds,
    DateTimeOffset FirstStartedOn,
    DateTimeOffset LastEndedOn,
    EpisodeStatus Status,
    decimal TotalCost,
    string? RootCauseIntent,
    string? RootCauseTopic,
    string? RootCauseQueue,
    IReadOnlyList<Channel> Channels)
{
    public int ContactCount => InteractionIds.Count;
}

public sealed record EvaluationResult(
    IReadOnlyList<InteractionEvaluation> Interactions,
    IReadOnlyList<Episode> Episodes,
    string EngineVersion);

/// <summary>
/// Pure, deterministic core of the accelerator. Given every interaction for a customer inside the look-back horizon,
/// it links same-issue repeats, stitches episodes and assigns a verified outcome to each interaction.
///
/// Design properties that matter at enterprise scale:
///  * Deterministic and idempotent – re-running over the same inputs yields identical keys and verdicts, so the
///    host can safely retry, replay Service Bus messages or backfill history.
///  * Partitioned by customer key – work for one customer never touches another's, enabling Service Bus sessions
///    (SessionId = customer key) for ordered, lock-free horizontal scale.
///  * No I/O – trivially unit-testable and reusable from Functions, Fabric notebooks or a Dataverse plug-in.
/// </summary>
public sealed class ResolutionEngine
{
    public const string Version = "1.0.0";

    private readonly LedgerPolicy _policy;
    private readonly SameIssueScorer _scorer;
    private readonly CostRateCard _rates;

    public ResolutionEngine(LedgerPolicy policy, CostRateCard rates, SameIssueScorer? scorer = null)
    {
        _policy = policy;
        _rates = rates;
        _scorer = scorer ?? new SameIssueScorer(policy);
    }

    public EvaluationResult Evaluate(IEnumerable<Interaction> interactions, DateTimeOffset asOf)
    {
        var all = interactions
            .GroupBy(i => (i.SourceSystem, i.SourceRecordId))   // defensive de-duplication
            .Select(g => g.OrderByDescending(x => x.EndedOn).First())
            .OrderBy(i => i.StartedOn).ThenBy(i => i.Id)
            .ToList();

        var identities = all.ToDictionary(i => i.Id, i => i.Customer.ResolveKey());

        var evaluations = new List<InteractionEvaluation>(all.Count);
        var episodes = new List<Episode>();

        // Partition by customer key; interactions without a usable key are singletons.
        var partitions = all.GroupBy(i =>
        {
            var (key, conf) = identities[i.Id];
            return key is not null && conf >= _policy.MinimumLinkConfidence ? key : $"solo:{i.Id:N}";
        });

        foreach (var partition in partitions)
        {
            var (evs, eps) = EvaluatePartition(partition.ToList(), identities, asOf);
            evaluations.AddRange(evs);
            episodes.AddRange(eps);
        }

        return new EvaluationResult(
            evaluations.OrderBy(e => e.InteractionId).ToList(),
            episodes.OrderBy(e => e.FirstStartedOn).ToList(),
            Version);
    }

    private (List<InteractionEvaluation>, List<Episode>) EvaluatePartition(
        List<Interaction> items,
        Dictionary<Guid, (string? Key, IdentityConfidence Confidence)> identities,
        DateTimeOffset asOf)
    {
        var predecessor = new Dictionary<Guid, (Guid Id, SameIssueAssessment Assessment)>();
        var successor = new Dictionary<Guid, Guid>();

        var linkable = items.All(i => identities[i.Id].Confidence >= _policy.MinimumLinkConfidence);

        if (linkable)
        {
            for (var j = 1; j < items.Count; j++)
            {
                var later = items[j];
                (Guid Id, SameIssueAssessment A)? best = null;

                // Walk backwards so that, among matches, the most recent prior interaction wins (chain semantics).
                for (var i = j - 1; i >= 0; i--)
                {
                    var earlier = items[i];
                    if (later.StartedOn <= earlier.StartedOn) continue;
                    if (later.StartedOn > earlier.EndedOn + _policy.WindowFor(earlier)) continue;

                    var a = _scorer.Score(earlier, later);
                    if (!a.IsSameIssue) continue;
                    if (best is null || a.Score > best.Value.A.Score + 0.15) best = (earlier.Id, a);
                }

                if (best is { } b)
                {
                    predecessor[later.Id] = (b.Id, b.A);
                    // First repeat wins as "the" successor; later repeats chain from their own predecessor.
                    successor.TryAdd(b.Id, later.Id);
                }
            }
        }

        // Union chains into episodes, rooted at the earliest interaction.
        var root = new Dictionary<Guid, Guid>();
        foreach (var it in items)
        {
            var cur = it.Id;
            while (predecessor.TryGetValue(cur, out var p)) cur = p.Id;
            root[it.Id] = cur;
        }

        var byId = items.ToDictionary(i => i.Id);
        var evaluations = new List<InteractionEvaluation>(items.Count);

        foreach (var it in items)
        {
            var (key, conf) = identities[it.Id];
            var windowCloses = it.EndedOn + _policy.WindowFor(it);
            var hasSuccessor = successor.TryGetValue(it.Id, out var succId);
            predecessor.TryGetValue(it.Id, out var pred);
            var linkSignals = hasSuccessor
                ? predecessor[succId].Assessment.Signals
                : (IReadOnlyList<SignalContribution>)Array.Empty<SignalContribution>();

            OutcomeVerdict verdict;
            string code, message;
            var identityLinkable = conf >= _policy.MinimumLinkConfidence;
            // Unlinkable interactions can never gain evidence, so their verdict is final immediately.
            var isFinal = hasSuccessor || asOf >= windowCloses || !identityLinkable;

            if (!identityLinkable)
            {
                verdict = OutcomeVerdict.Unknown;
                code = "NoReliableIdentity";
                message = $"Customer identity confidence '{conf}' is below the linking minimum; repeats cannot be verified.";
            }
            else if (hasSuccessor)
            {
                var s = byId[succId];
                var score = predecessor[succId].Assessment.Score;
                if (it.FollowUpScheduled)
                {
                    verdict = OutcomeVerdict.PlannedFollowUp;
                    code = "PlannedFollowUp";
                    message = $"Same-issue contact after {Hours(s.StartedOn - it.EndedOn)} was a scheduled follow-up (score {score:F2}).";
                }
                else if (it.IsBotOnly)
                {
                    verdict = OutcomeVerdict.FalseContainment;
                    code = "RepeatAfterBot";
                    message = $"Bot reported '{it.NativeBotOutcome}' but the customer returned via {s.Channel} after {Hours(s.StartedOn - it.EndedOn)} about the same issue (score {score:F2}).";
                }
                else
                {
                    verdict = OutcomeVerdict.FailedHumanResolution;
                    code = "RepeatAfterHuman";
                    message = $"Customer returned via {s.Channel} after {Hours(s.StartedOn - it.EndedOn)} about the same issue (score {score:F2}).";
                }
            }
            else if (!isFinal)
            {
                verdict = OutcomeVerdict.Pending;
                code = "WindowOpen";
                message = $"No repeat yet; verdict matures at {windowCloses:u}.";
            }
            else if (it.IsBotOnly && it.NativeBotOutcome == NativeBotOutcome.Abandoned && _policy.TreatSilentAbandonmentAsUnknown)
            {
                verdict = OutcomeVerdict.Unknown;
                code = "SilentAbandonment";
                message = "Customer abandoned the bot and did not return; counted as deflected natively, but resolution is unverified.";
            }
            else
            {
                verdict = OutcomeVerdict.VerifiedResolved;
                code = "NoRepeatInWindow";
                message = $"No same-issue contact within {Hours(_policy.WindowFor(it))} window.";
            }

            evaluations.Add(new InteractionEvaluation(
                it.Id, key, conf, verdict, isFinal,
                pred.Id == Guid.Empty ? null : pred.Id,
                pred.Id == Guid.Empty ? null : pred.Assessment.Score,
                hasSuccessor ? succId : null,
                byId[root[it.Id]].ExistingEpisodeKey ?? EpisodeKeyFor(root[it.Id]),
                CostCalculator.Price(it, _rates),
                new VerdictReason(code, message, windowCloses, linkSignals)));
        }

        var evalById = evaluations.ToDictionary(e => e.InteractionId);
        var episodes = items
            .GroupBy(i => root[i.Id])
            .Select(g => BuildEpisode(g.OrderBy(i => i.StartedOn).ToList(), evalById))
            .ToList();

        return (evaluations, episodes);
    }

    private static Episode BuildEpisode(List<Interaction> members, Dictionary<Guid, InteractionEvaluation> evals)
    {
        var last = evals[members[^1].Id];
        var status = last.Verdict switch
        {
            OutcomeVerdict.Pending => EpisodeStatus.Open,
            OutcomeVerdict.VerifiedResolved => EpisodeStatus.Resolved,
            OutcomeVerdict.Unknown when members.Count > 1 => EpisodeStatus.Unresolved,
            _ => EpisodeStatus.Unknown,
        };

        var firstFailure = members.FirstOrDefault(m =>
            evals[m.Id].Verdict is OutcomeVerdict.FalseContainment or OutcomeVerdict.FailedHumanResolution);

        return new Episode(
            last.EpisodeKey,
            last.CustomerKey,
            members.Select(m => m.Id).ToList(),
            members[0].StartedOn,
            members.Max(m => m.EndedOn),
            status,
            members.Sum(m => evals[m.Id].Cost.Total),
            firstFailure?.IntentCode ?? members[0].IntentCode,
            firstFailure?.BotTopic,
            firstFailure?.QueueName,
            members.Select(m => m.Channel).Distinct().ToList());
    }

    /// <summary>Deterministic, stable across re-runs: derived from the root interaction.</summary>
    public static string EpisodeKeyFor(Guid rootInteractionId) => $"EP-{rootInteractionId:N}";

    private static string Hours(TimeSpan t) =>
        t.TotalHours >= 48 ? $"{t.TotalDays:F1}d" : $"{Math.Max(0, t.TotalHours):F1}h";
}
