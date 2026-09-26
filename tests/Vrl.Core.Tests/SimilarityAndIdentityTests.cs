using Vrl.Core.Costing;
using Vrl.Core.Model;
using Vrl.Core.Policy;
using Vrl.Core.Similarity;
using static Vrl.Core.Tests.Build;

namespace Vrl.Core.Tests;

public class SimilarityAndIdentityTests
{
    private readonly SameIssueScorer _scorer = new(LedgerPolicy.Default);

    [Fact]
    public void Shared_order_reference_plus_family_is_same_issue()
    {
        var a = Contact(0, Alice, intent: "orders.change", summary: "Wants to change address on order ORD-558812");
        var b = Contact(1, Alice, intent: "orders.cancel", summary: "Asked to cancel ORD 558812 instead");
        var s = _scorer.Score(a, b);
        Assert.True(s.IsSameIssue);
        Assert.Contains(s.Signals, x => x.Signal == "SharedReference");
    }

    [Fact]
    public void Typographic_dashes_do_not_hide_a_reference()
    {
        // Seen live: text pasted into the chat widget carried U+2011 (non-breaking hyphen) instead of '-'.
        var refs = ReferenceExtractor.Default.Extract("My parcel TRK\u201155667788 shows delivered");
        Assert.Contains("TRK55667788", refs);
    }

    [Fact]
    public void Structured_reference_alone_needs_corroboration()
    {
        var a = Contact(0, Alice, intent: null, summary: "Refund for ORD-4455667 not received");
        var b = Contact(1, Alice, intent: null, summary: "Where is my delivery for ORD-4455667");
        var s = _scorer.Score(a, b);
        Assert.Equal(0.50, s.Score, 3);
        Assert.False(s.IsSameIssue);
    }

    [Fact]
    public void Structured_reference_outweighs_bare_number()
    {
        var structured = _scorer.Score(Contact(0, Alice, intent: null, summary: "TRK-55667788 missing"),
                                       Contact(1, Alice, intent: null, summary: "TRK-55667788 still missing"));
        var bare = _scorer.Score(Contact(0, Alice, intent: null, summary: "tracking 55667788 missing"),
                                 Contact(1, Alice, intent: null, summary: "tracking 55667788 still missing"));
        Assert.True(structured.Score > bare.Score);
    }

    [Fact]
    public void Customer_phone_number_is_not_treated_as_reference()
    {
        var a = Contact(0, Alice, intent: null, summary: "Call back on 01512345678", phone: "+49 1512345678");
        var b = Contact(1, Alice, intent: null, summary: "Customer gave number 01512345678 again", phone: "+49 1512345678");
        Assert.DoesNotContain(_scorer.Score(a, b).Signals, x => x.Signal == "SharedReference");
    }

    [Fact]
    public void Intent_conflict_penalises_score()
    {
        var caseId = Guid.NewGuid();
        var a = Contact(0, Alice, intent: "billing.refund", caseId: caseId);
        var b = Contact(1, Alice, intent: "delivery.late", caseId: caseId);
        var s = _scorer.Score(a, b);
        Assert.Equal(0.40, s.Score, 3);
        Assert.False(s.IsSameIssue);
    }

    [Fact]
    public void Same_case_alone_is_same_issue()
    {
        var caseId = Guid.NewGuid();
        var a = Contact(0, Alice, intent: null, caseId: caseId, channel: Channel.Case);
        var b = Contact(1, Alice, intent: null, caseId: caseId);
        Assert.True(_scorer.Score(a, b).IsSameIssue);
    }

    [Fact]
    public void Vectors_from_different_models_are_never_compared()
    {
        var v = new LocalHashingEmbeddingProvider().Embed("router keeps dropping wifi");
        var a = Contact(0, Alice, intent: null) with { Embedding = v, EmbeddingModel = "local-hash-512-v1" };
        var b = Contact(1, Alice, intent: null) with { Embedding = v, EmbeddingModel = "aoai:text-embedding-3-small:512" };
        Assert.DoesNotContain(_scorer.Score(a, b).Signals, x => x.Signal == "Semantic");
        Assert.Contains(_scorer.Score(a, b with { EmbeddingModel = "local-hash-512-v1" }).Signals, x => x.Signal == "Semantic");
    }

    [Fact]
    public void Unrelated_texts_have_low_cosine()
    {
        var p = new LocalHashingEmbeddingProvider();
        var cos = VectorMath.Cosine(p.Embed("refund for damaged blender not received"), p.Embed("change delivery address for sofa"));
        Assert.True(cos < 0.3, $"cos={cos}");
    }

    [Fact]
    public void Embedding_roundtrips_through_base64()
    {
        var v = new LocalHashingEmbeddingProvider(64).Embed("hello world order");
        Assert.Equal(v, VectorMath.FromBase64(VectorMath.ToBase64(v)));
        Assert.Null(VectorMath.FromBase64("not-base64!"));
    }

    [Theory]
    [InlineData("+49 (0)151-234 5678", "+4901512345678")]
    [InlineData("0049 151 2345678", "+491512345678")]
    [InlineData("12 34", null)]
    public void Phone_normalisation(string input, string? expected) =>
        Assert.Equal(expected, CustomerIdentity.NormalizePhone(input));

    [Fact]
    public void Identity_prefers_contact_and_hashes_pii()
    {
        var (key, conf) = new CustomerIdentity(Email: "Jane.Doe@Example.com").ResolveKey();
        Assert.StartsWith("email:", key);
        Assert.DoesNotContain("jane", key);
        Assert.Equal(IdentityConfidence.Medium, conf);

        Assert.Equal(IdentityConfidence.High, new CustomerIdentity(ContactId: Alice, Email: "x@y.z").ResolveKey().Confidence);
        Assert.Equal(IdentityConfidence.Low, new CustomerIdentity(AccountId: Bob).ResolveKey().Confidence);
    }

    [Fact]
    public void Cost_uses_queue_rate_and_voice_telephony()
    {
        var rates = new CostRateCard
        {
            CreditPrice = 0.01m, TelephonyPerMinute = 0.02m, LaborPerHour = 30m,
            QueueLaborPerHour = new Dictionary<string, decimal> { ["Tier2"] = 60m },
        };
        var i = Contact(0, Alice, channel: Channel.Voice, queue: "Tier2", handleMinutes: 30, credits: 70) with { TelephonyMinutes = 32 };
        var c = CostCalculator.Price(i, rates);
        Assert.Equal(0.70m, c.Ai);
        Assert.Equal(0.64m, c.Telephony);
        Assert.Equal(30m, c.Labor);
        Assert.Equal(31.34m, c.Total);
    }
}
