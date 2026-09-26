using Vrl.Core.Engine;
using Vrl.Core.Model;

namespace Vrl.Core.Analytics;

public sealed record Breakdown(string Dimension, string Value, int Interactions, int Repeats, int FalseContainment, decimal Cost);

public sealed record LedgerKpis
{
    public int Interactions { get; init; }
    public int DeterminedInteractions { get; init; }

    /// <summary>Decided contacts with no reliable customer identity; excluded from repeat and root-cause rates.</summary>
    public int UnverifiableInteractions { get; init; }
    public int PendingInteractions { get; init; }

    public int BotOnlyDetermined { get; init; }

    /// <summary>All decided conversations the AI agent took part in (bot-only + escalated). Denominator for containment.</summary>
    public int BotConversationsDetermined { get; init; }

    /// <summary>
    /// Microsoft's definition: bot deflected ÷ (deflected + escalated). A bot-only conversation counts as deflected whether
    /// the bot resolved it or the customer abandoned it.
    /// </summary>
    public double NativeDeflectionRate { get; init; }

    /// <summary>Bot-only conversations with a VerifiedResolved verdict, over the same denominator as native deflection.</summary>
    public double VerifiedContainmentRate { get; init; }

    /// <summary>The headline number: percentage points of reported containment that do not survive verification.</summary>
    public double ContainmentGapPoints => Math.Round((NativeDeflectionRate - VerifiedContainmentRate) * 100, 1);

    /// <summary>Share of determined interactions that were followed by a same-issue repeat.</summary>
    public double RepeatContactRate { get; init; }

    /// <summary>Closed episodes resolved on the first contact, over all closed episodes.</summary>
    public double InteractionFcr { get; init; }

    public decimal TotalCost { get; init; }
    public decimal CostPerContact { get; init; }
    public decimal CostPerResolvedOutcome { get; init; }

    /// <summary>Cost spent on contacts that were followed by a repeat — the avoidable spend.</summary>
    public decimal CostOfRepeats { get; init; }

    public IReadOnlyList<Breakdown> ByBotTopic { get; init; } = [];
    public IReadOnlyList<Breakdown> ByQueue { get; init; } = [];
    public IReadOnlyList<Breakdown> ByIntent { get; init; } = [];
    public IReadOnlyList<Breakdown> ByChannel { get; init; } = [];
}

/// <summary>
/// Computes ledger KPIs in memory – used for the synthetic demo, unit tests and the API.
/// The Power BI model computes the same measures over Dataverse / Fabric; keep definitions in sync (see docs/KPI-Definitions.md).
/// </summary>
public static class LedgerKpiCalculator
{
    public static LedgerKpis Calculate(IReadOnlyCollection<Interaction> interactions, EvaluationResult result)
    {
        var evals = result.Interactions.ToDictionary(e => e.InteractionId);
        var rows = interactions.Where(i => evals.ContainsKey(i.Id)).Select(i => (I: i, E: evals[i.Id])).ToList();
        var determined = rows.Where(r => r.E.Verdict != OutcomeVerdict.Pending).ToList();

        var bot = determined.Where(r => r.I.IsBotOnly).ToList();
        var botConversations = determined.Count(r => r.I.HandlingMode is HandlingMode.BotOnly or HandlingMode.BotThenHuman);
        var nativeDeflected = bot.Count;
        var verifiedBot = bot.Count(r => r.E.Verdict == OutcomeVerdict.VerifiedResolved);

        // Contacts that cannot be tied to a customer can never be seen to repeat. Keeping them in repeat and root-cause
        // denominators would flatter the result, so they are excluded there (and reported). They stay in the AI agent
        // denominators, because Microsoft's deflection counts them as deflected and the gap is the point.
        var verifiable = determined.Where(r => r.E.Reason.Code != "NoReliableIdentity").ToList();
        var repeats = verifiable.Count(r => r.E.SuccessorId is not null && r.E.Verdict != OutcomeVerdict.PlannedFollowUp);

        var closedEpisodes = result.Episodes.Where(e => e.Status != EpisodeStatus.Open).ToList();
        // FCR only over episodes whose outcome is known; single unidentifiable contacts (status Unknown) would dilute it.
        var outcomeKnown = closedEpisodes.Where(e => e.Status is EpisodeStatus.Resolved or EpisodeStatus.Unresolved).ToList();
        var fcr = outcomeKnown.Count(e => e.Status == EpisodeStatus.Resolved && e.ContactCount == 1);
        var resolvedEpisodes = closedEpisodes.Count(e => e.Status == EpisodeStatus.Resolved);

        var totalCost = rows.Sum(r => r.E.Cost.Total);
        var closedCost = closedEpisodes.Sum(e => e.TotalCost);

        return new LedgerKpis
        {
            Interactions = rows.Count,
            DeterminedInteractions = determined.Count,
            PendingInteractions = rows.Count - determined.Count,
            BotOnlyDetermined = bot.Count,
            BotConversationsDetermined = botConversations,
            NativeDeflectionRate = Ratio(nativeDeflected, botConversations),
            VerifiedContainmentRate = Ratio(verifiedBot, botConversations),
            UnverifiableInteractions = determined.Count - verifiable.Count,
            RepeatContactRate = Ratio(repeats, verifiable.Count),
            InteractionFcr = Ratio(fcr, outcomeKnown.Count),
            TotalCost = totalCost,
            CostPerContact = rows.Count == 0 ? 0 : Math.Round(totalCost / rows.Count, 2),
            CostPerResolvedOutcome = resolvedEpisodes == 0 ? 0 : Math.Round(closedCost / resolvedEpisodes, 2),
            CostOfRepeats = determined.Where(r => r.E.SuccessorId is not null).Sum(r => r.E.Cost.Total),
            ByBotTopic = Break("BotTopic", verifiable.Where(r => r.I.IsBotOnly), r => r.I.BotTopic),
            ByQueue = Break("Queue", verifiable.Where(r => !r.I.IsBotOnly), r => r.I.QueueName),
            ByIntent = Break("Intent", verifiable, r => r.I.IntentCode),
            ByChannel = Break("Channel", verifiable, r => r.I.Channel.ToString()),
        };
    }

    private static IReadOnlyList<Breakdown> Break(
        string dimension,
        IEnumerable<(Interaction I, InteractionEvaluation E)> rows,
        Func<(Interaction I, InteractionEvaluation E), string?> selector) =>
        rows.GroupBy(r => selector(r) ?? "(none)")
            .Select(g => new Breakdown(
                dimension,
                g.Key,
                g.Count(),
                g.Count(r => r.E.SuccessorId is not null),
                g.Count(r => r.E.Verdict == OutcomeVerdict.FalseContainment),
                g.Sum(r => r.E.Cost.Total)))
            .OrderByDescending(b => b.Repeats)
            .ThenByDescending(b => b.Interactions)
            .ToList();

    private static double Ratio(int n, int d) => d == 0 ? 0 : Math.Round((double)n / d, 4);
}
