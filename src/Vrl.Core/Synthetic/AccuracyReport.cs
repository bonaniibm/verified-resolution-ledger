using Vrl.Core.Engine;
using Vrl.Core.Model;

namespace Vrl.Core.Synthetic;

/// <summary>Compares engine verdicts with synthetic ground truth – the evidence that the ledger's numbers can be trusted.</summary>
public sealed record AccuracyReport(
    int Evaluated,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    int OutOfScopeRepeats,
    double RepeatPrecision,
    double RepeatRecall,
    double TrueContainmentRate,
    double VerifiedContainmentRate,
    double NativeDeflectionRate)
{
    /// <summary>
    /// Recall is measured only over repeats the ledger is designed to catch: identifiable customer and a return inside
    /// the policy window. Repeats outside that scope are reported separately (they are a policy choice, not an engine miss).
    /// </summary>
    public static AccuracyReport Compute(SyntheticDataset data, EvaluationResult result, TimeSpan window)
    {
        var byId = data.Interactions.ToDictionary(i => i.Id);
        int tp = 0, fp = 0, fn = 0, n = 0, outOfScope = 0;

        foreach (var e in result.Interactions.Where(e => e.Verdict != OutcomeVerdict.Pending))
        {
            if (!data.Truth.TryGetValue(e.InteractionId, out var t)) continue;
            n++;
            var predicted = e.SuccessorId is not null;
            if (predicted && t.FollowedBySameIssueContact) tp++;
            else if (predicted) fp++;
            else if (t.FollowedBySameIssueContact && t.IdentityLinkable && t.GapToNextContact <= window) fn++;
            else if (t.FollowedBySameIssueContact) outOfScope++;
        }

        var decided = result.Interactions.Where(e => e.Verdict != OutcomeVerdict.Pending).ToList();
        var botConversations = decided.Count(e => byId[e.InteractionId].HandlingMode is HandlingMode.BotOnly or HandlingMode.BotThenHuman);
        var bot = decided.Where(e => byId[e.InteractionId].IsBotOnly).ToList();
        double Rate(int x) => botConversations == 0 ? 0 : Math.Round((double)x / botConversations, 4);

        return new AccuracyReport(
            n, tp, fp, fn, outOfScope,
            tp + fp == 0 ? 0 : Math.Round((double)tp / (tp + fp), 4),
            tp + fn == 0 ? 0 : Math.Round((double)tp / (tp + fn), 4),
            Rate(bot.Count(e => data.Truth[e.InteractionId].IssueActuallyResolvedHere)),
            Rate(bot.Count(e => e.Verdict == OutcomeVerdict.VerifiedResolved)),
            Rate(bot.Count));
    }
}
