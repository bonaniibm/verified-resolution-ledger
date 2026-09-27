using Vrl.Core.Abstractions;
using Vrl.Core.Costing;
using Vrl.Core.Model;
using Vrl.Core.Policy;
using Vrl.Core.Processing;
using Vrl.Core.Similarity;
using Vrl.Core.Util;
using static Vrl.Core.Tests.Build;

namespace Vrl.Core.Tests;

public class LedgerProcessorTests
{
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (LedgerProcessor, InMemoryLedgerRepository, FakeTime) Create(DateTimeOffset now)
    {
        var repo = new InMemoryLedgerRepository();
        var time = new FakeTime(now);
        var cfg = new StaticLedgerConfigurationProvider(LedgerPolicy.Default, CostRateCard.Default);
        return (new LedgerProcessor(repo, cfg, new LocalHashingEmbeddingProvider(), time), repo, time);
    }

    private static Interaction Raw(Interaction i) => i with { Id = Guid.Empty };

    [Fact]
    public async Task Incremental_ingest_produces_same_result_as_batch_and_matures()
    {
        var bot = Contact(0, Alice, HandlingMode.BotOnly, NativeBotOutcome.ResolvedImplied);
        var human = Contact(20, Alice);
        var (p, repo, time) = Create(bot.EndedOn);

        await p.IngestAsync(Raw(bot));
        var botId = DeterministicGuid.ForInteraction(bot.SourceSystem, bot.SourceRecordId);
        Assert.Equal(OutcomeVerdict.Pending, repo.Evaluations.Single(e => e.InteractionId == botId).Verdict);

        time.Now = human.EndedOn;
        await p.IngestAsync(Raw(human));
        Assert.Equal(OutcomeVerdict.FalseContainment, repo.Evaluations.Single(e => e.InteractionId == botId).Verdict);
        Assert.Single(repo.Episodes);

        time.Now = human.EndedOn.AddDays(4);
        Assert.Equal(1, await p.MatureDueVerdictsAsync());
        Assert.All(repo.Evaluations, e => Assert.True(e.IsFinal));
        Assert.Equal(EpisodeStatus.Resolved, repo.Episodes.Single().Status);
        Assert.Empty(await repo.FindCustomerKeysDueForMaturationAsync(time.Now, 10));
    }

    [Fact]
    public async Task Replaying_the_same_message_is_idempotent()
    {
        var a = Contact(0, Alice);
        var (p, repo, _) = Create(a.EndedOn);
        await p.IngestAsync(Raw(a));
        var writes = repo.WriteCount;
        await p.IngestAsync(Raw(a));
        Assert.Single(repo.Interactions);
        Assert.Equal(writes, repo.WriteCount);
    }

    [Fact]
    public async Task Stored_interaction_keeps_no_raw_phone_or_email()
    {
        var a = Contact(0, null, phone: "+49 151 23456789", summary: "Call me on 0151 23456789 or jane@contoso.com re ORD-1234567");
        var (p, repo, _) = Create(a.EndedOn);
        await p.IngestAsync(Raw(a));
        var stored = repo.Interactions.Single();
        Assert.Null(stored.Customer.Phone);
        Assert.DoesNotContain("23456789", stored.Summary);
        Assert.DoesNotContain("jane@", stored.Summary);
        Assert.Contains("ORD-1234567", stored.Summary);
        Assert.StartsWith("phone:", stored.Customer.ResolveKey().Key);
    }

    [Fact]
    public async Task Anonymous_contact_is_final_unknown_immediately()
    {
        var a = Contact(0, null);
        var (p, repo, _) = Create(a.EndedOn);
        await p.IngestAsync(Raw(a));
        var e = repo.Evaluations.Single();
        Assert.Equal(OutcomeVerdict.Unknown, e.Verdict);
        Assert.True(e.IsFinal);
    }

    [Fact]
    public void Deterministic_guid_is_stable_and_v5()
    {
        var g1 = DeterministicGuid.ForInteraction("omnichannel", "ABC");
        var g2 = DeterministicGuid.ForInteraction("OmniChannel", "abc");
        Assert.Equal(g1, g2);
        Assert.Equal('5', g1.ToString()[14]);
    }

    private sealed class RecordingEmbeddings : IEmbeddingProvider
    {
        public List<string> Texts { get; } = [];
        public string ModelId => "test";
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Texts.Add(text);
            return Task.FromResult(new float[] { 1, 0 });
        }
    }

    [Theory]
    [InlineData("My parcel TRK-1234567 never arrived", "My parcel TRK-1234567 never arrived")]
    [InlineData(null, "Hello, I'm the bot. How can I help? My parcel TRK-1234567 never arrived")]
    public async Task Similarity_uses_the_customers_own_words_when_available(string? issue, string expectedEmbedded)
    {
        var embeddings = new RecordingEmbeddings();
        var p = new LedgerProcessor(new InMemoryLedgerRepository(),
            new StaticLedgerConfigurationProvider(LedgerPolicy.Default, CostRateCard.Default), embeddings, new FakeTime(T0));
        await p.IngestAsync(Raw(Contact(0, Alice, summary: "Hello, I'm the bot. How can I help? My parcel TRK-1234567 never arrived")
            with { IssueText = issue }));
        Assert.Equal(expectedEmbedded, Assert.Single(embeddings.Texts));
    }

    [Fact]
    public async Task Late_maturation_keeps_the_whole_episode()
    {
        // A came back as B (same issue): A's verdict is final, B's window is still open. If maturation runs long after
        // B's window closed (timer outage, backlog, replay), the history query returns B but not A. The episode must
        // still be rebuilt with both contacts, and B must keep its predecessor.
        var a = Contact(0, Alice, intent: "billing.refund");
        var b = Contact(20, Alice, intent: "billing.refund");
        var (p, repo, time) = Create(a.EndedOn);
        await p.IngestAsync(Raw(a));
        time.Now = b.EndedOn;
        await p.IngestAsync(Raw(b));
        var aId = DeterministicGuid.ForInteraction(a.SourceSystem, a.SourceRecordId);
        var bId = DeterministicGuid.ForInteraction(b.SourceSystem, b.SourceRecordId);
        Assert.True(repo.Evaluations.Single(e => e.InteractionId == aId).IsFinal);
        var episodeKey = repo.Evaluations.Single(e => e.InteractionId == aId).EpisodeKey;

        time.Now = b.EndedOn.AddDays(30);
        Assert.Equal(1, await p.MatureDueVerdictsAsync());

        var episode = repo.Episodes.Single(e => e.EpisodeKey == episodeKey);
        Assert.Equal(2, episode.ContactCount);
        Assert.Equal(EpisodeStatus.Resolved, episode.Status);
        var bEval = repo.Evaluations.Single(e => e.InteractionId == bId);
        Assert.Equal(aId, bEval.PredecessorId);
        Assert.Equal(OutcomeVerdict.VerifiedResolved, bEval.Verdict);
        Assert.True(bEval.IsFinal);
        Assert.Equal(OutcomeVerdict.FailedHumanResolution, repo.Evaluations.Single(e => e.InteractionId == aId).Verdict);
    }
}
