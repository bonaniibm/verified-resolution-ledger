using Vrl.Core.Analytics;
using Vrl.Core.Costing;
using Vrl.Core.Engine;
using Vrl.Core.Model;
using Vrl.Core.Policy;
using Vrl.Core.Similarity;
using static Vrl.Core.Tests.Build;

namespace Vrl.Core.Tests;

public class ResolutionEngineTests
{
    private static readonly ResolutionEngine Engine = new(LedgerPolicy.Default, CostRateCard.Default);
    private static DateTimeOffset Later => T0.AddDays(30);

    private static InteractionEvaluation Eval(EvaluationResult r, Interaction i) =>
        r.Interactions.Single(e => e.InteractionId == i.Id);

    [Fact]
    public void Bot_resolved_then_same_issue_repeat_is_false_containment()
    {
        var bot = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.ResolvedImplied, topic: "Refund status");
        var human = Contact(20, Alice, HandlingMode.HumanOnly, channel: Channel.Voice);

        var r = Engine.Evaluate([bot, human], Later);

        var e = Eval(r, bot);
        Assert.Equal(OutcomeVerdict.FalseContainment, e.Verdict);
        Assert.True(e.IsFinal);
        Assert.Equal(human.Id, e.SuccessorId);
        Assert.Contains(e.Reason.LinkSignals, s => s.Signal == "IntentExact");
        Assert.Equal(OutcomeVerdict.VerifiedResolved, Eval(r, human).Verdict);
        Assert.Single(r.Episodes);
        Assert.Equal(2, r.Episodes[0].ContactCount);
        Assert.Equal("Refund status", r.Episodes[0].RootCauseTopic);
    }

    [Fact]
    public void Human_contact_followed_by_repeat_is_failed_human_resolution()
    {
        var a = Contact(0, Alice);
        var b = Contact(30, Alice);
        var r = Engine.Evaluate([a, b], Later);
        Assert.Equal(OutcomeVerdict.FailedHumanResolution, Eval(r, a).Verdict);
    }

    [Fact]
    public void Repeat_outside_window_is_not_linked()
    {
        var a = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.Resolved);
        var b = Contact(24 * 4, Alice); // 96h > 72h default
        var r = Engine.Evaluate([a, b], Later);
        Assert.Equal(OutcomeVerdict.VerifiedResolved, Eval(r, a).Verdict);
        Assert.Equal(2, r.Episodes.Count);
    }

    [Fact]
    public void Different_issue_is_not_a_repeat()
    {
        var a = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.Resolved, intent: "billing.refund");
        var b = Contact(5, Alice, intent: "delivery.late");
        var r = Engine.Evaluate([a, b], Later);
        Assert.Equal(OutcomeVerdict.VerifiedResolved, Eval(r, a).Verdict);
    }

    [Fact]
    public void Different_customers_are_never_linked()
    {
        var a = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.Resolved);
        var b = Contact(2, Bob);
        var r = Engine.Evaluate([a, b], Later);
        Assert.Equal(OutcomeVerdict.VerifiedResolved, Eval(r, a).Verdict);
    }

    [Fact]
    public void Open_window_without_repeat_is_pending()
    {
        var a = Contact(0, Alice);
        var r = Engine.Evaluate([a], T0.AddHours(10));
        var e = Eval(r, a);
        Assert.Equal(OutcomeVerdict.Pending, e.Verdict);
        Assert.False(e.IsFinal);
        Assert.Equal(EpisodeStatus.Open, r.Episodes[0].Status);
    }

    [Fact]
    public void Repeat_finalises_verdict_before_window_closes()
    {
        var a = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.Resolved);
        var b = Contact(2, Alice);
        var r = Engine.Evaluate([a, b], T0.AddHours(3));
        Assert.Equal(OutcomeVerdict.FalseContainment, Eval(r, a).Verdict);
        Assert.Equal(OutcomeVerdict.Pending, Eval(r, b).Verdict);
    }

    [Fact]
    public void Scheduled_follow_up_is_not_a_failure()
    {
        var a = Contact(0, Alice, followUp: true);
        var b = Contact(24, Alice, channel: Channel.Voice);
        var r = Engine.Evaluate([a, b], Later);
        Assert.Equal(OutcomeVerdict.PlannedFollowUp, Eval(r, a).Verdict);
    }

    [Fact]
    public void Silent_bot_abandonment_is_unknown_not_resolved()
    {
        var a = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.Abandoned);
        var r = Engine.Evaluate([a], Later);
        Assert.Equal(OutcomeVerdict.Unknown, Eval(r, a).Verdict);
        Assert.Equal("SilentAbandonment", Eval(r, a).Reason.Code);
    }

    [Fact]
    public void Anonymous_contacts_are_unknown()
    {
        var a = Contact(0, contact: null, HandlingMode.BotOnly, NativeBotOutcome.Resolved);
        var r = Engine.Evaluate([a], Later);
        Assert.Equal(OutcomeVerdict.Unknown, Eval(r, a).Verdict);
        Assert.Equal(IdentityConfidence.None, Eval(r, a).IdentityConfidence);
    }

    [Fact]
    public void Phone_identity_links_across_formats()
    {
        var a = Contact(0, null, HandlingMode.BotOnly, NativeBotOutcome.Resolved, channel: Channel.Voice, phone: "+49 (0)151 2345-6789");
        var b = Contact(3, null, channel: Channel.Voice, phone: "+49015123456789");
        var r = Engine.Evaluate([a, b], Later);
        Assert.Equal(OutcomeVerdict.FalseContainment, Eval(r, a).Verdict);
    }

    [Fact]
    public void Chain_of_three_forms_one_episode_rooted_at_first()
    {
        var a = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.Resolved);
        var b = Contact(10, Alice);
        var c = Contact(40, Alice);
        var r = Engine.Evaluate([c, a, b], Later); // order must not matter

        Assert.Single(r.Episodes);
        Assert.Equal(ResolutionEngine.EpisodeKeyFor(a.Id), r.Episodes[0].EpisodeKey);
        Assert.Equal(b.Id, Eval(r, c).PredecessorId);
        Assert.Equal(OutcomeVerdict.FailedHumanResolution, Eval(r, b).Verdict);
        Assert.Equal(OutcomeVerdict.VerifiedResolved, Eval(r, c).Verdict);
        Assert.Equal(EpisodeStatus.Resolved, r.Episodes[0].Status);
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var items = new[] { Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.Resolved), Contact(5, Alice), Contact(8, Bob) };
        var r1 = Engine.Evaluate(items, Later);
        var r2 = Engine.Evaluate(items.Reverse(), Later);
        Assert.Equal(r1.Interactions, r2.Interactions, new EvalComparer());
        Assert.Equal(r1.Episodes.Select(e => e.EpisodeKey), r2.Episodes.Select(e => e.EpisodeKey));
    }

    [Fact]
    public void Existing_episode_key_is_preserved()
    {
        var b = Contact(10, Alice) with { ExistingEpisodeKey = "EP-legacy" };
        var c = Contact(20, Alice);
        var r = Engine.Evaluate([b, c], Later);
        Assert.All(r.Interactions, e => Assert.Equal("EP-legacy", e.EpisodeKey));
    }

    [Fact]
    public void Semantic_similarity_plus_intent_family_links_paraphrased_contacts()
    {
        var provider = new LocalHashingEmbeddingProvider();
        var policy = new LedgerPolicy { Weights = SimilarityWeights.ForEmbeddingModel(provider.ModelId) };
        var engine = new ResolutionEngine(policy, CostRateCard.Default);
        const string s1 = "Customer reports the router keeps dropping the wifi connection every evening after the firmware update";
        const string s2 = "Router keeps dropping the wifi connection every evening since the firmware update, customer frustrated";
        var a = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.ResolvedImplied, intent: "network.wifi", summary: s1) with { Embedding = provider.Embed(s1) };
        var b = Contact(6, Alice, intent: "network.outage", summary: s2) with { Embedding = provider.Embed(s2) };
        Assert.Equal(OutcomeVerdict.VerifiedResolved, Eval(Engine.Evaluate([a with { Embedding = null }, b with { Embedding = null }], Later), a).Verdict);
        var r = engine.Evaluate([a, b], Later);
        Assert.Equal(OutcomeVerdict.FalseContainment, Eval(r, a).Verdict);
        Assert.Contains(Eval(r, a).Reason.LinkSignals, s => s.Signal == "Semantic");
    }

    [Fact]
    public void Kpis_expose_containment_gap()
    {
        var items = new List<Interaction>
        {
            // Bot "resolved" but came back → false containment
            Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.ResolvedImplied, topic: "Refund", credits: 10),
            Contact(5, Alice, handleMinutes: 12, queue: "Billing"),
            // Bot resolved, genuinely
            Contact(0, Bob, HandlingMode.BotOnly, NativeBotOutcome.Resolved, intent: "delivery.track", topic: "Track", credits: 6),
            // Bot abandoned, silent
            Contact(1, Guid.NewGuid(), HandlingMode.BotOnly, NativeBotOutcome.Abandoned, topic: "Track", credits: 2),
            // Escalated in-conversation: counts in the denominator, never as deflected
            Contact(2, Guid.NewGuid(), HandlingMode.BotThenHuman, NativeBotOutcome.Escalated, intent: "account.password.reset", topic: "Reset", credits: 4, handleMinutes: 6),
        };

        var r = Engine.Evaluate(items, Later);
        var k = LedgerKpiCalculator.Calculate(items, r);

        Assert.Equal(3, k.BotOnlyDetermined);
        Assert.Equal(4, k.BotConversationsDetermined);
        Assert.Equal(0.75, k.NativeDeflectionRate);
        Assert.Equal(0.25, k.VerifiedContainmentRate);
        Assert.Equal(50.0, k.ContainmentGapPoints);
        Assert.Equal("Refund", k.ByBotTopic.First().Value);
        Assert.True(k.CostPerResolvedOutcome > k.CostPerContact);
    }

    private sealed class EvalComparer : IEqualityComparer<InteractionEvaluation>
    {
        public bool Equals(InteractionEvaluation? x, InteractionEvaluation? y) =>
            x!.InteractionId == y!.InteractionId && x.Verdict == y.Verdict && x.EpisodeKey == y.EpisodeKey
            && x.PredecessorId == y.PredecessorId && x.SuccessorId == y.SuccessorId;

        public int GetHashCode(InteractionEvaluation obj) => obj.InteractionId.GetHashCode();
    }

    [Fact]
    public void Unidentifiable_contacts_do_not_dilute_repeat_rate_or_fcr()
    {
        // Alice repeats once; three anonymous contacts can never be seen to repeat and must not flatter the rates.
        var a1 = Contact(0, Alice);
        var a2 = Contact(5, Alice);
        var anon = new[] { Contact(1), Contact(2), Contact(3) };
        var all = new[] { a1, a2 }.Concat(anon).ToList();

        var k = LedgerKpiCalculator.Calculate(all, Engine.Evaluate(all, Later));

        Assert.Equal(3, k.UnverifiableInteractions);
        Assert.Equal(0.5, k.RepeatContactRate, 3);   // 1 repeat of 2 identifiable contacts, not 1 of 5
        Assert.Equal(0.0, k.InteractionFcr, 3);      // Alice's episode needed two contacts; anonymous ones are not "resolved first time"
    }
}
