using Vrl.Core.Model;
using Vrl.Core.Processing;

namespace Vrl.Core.Tests;

public class NormalizerTests
{
    private static Interaction Raw() => new()
    {
        Id = Guid.Empty,
        SourceSystem = "api",
        SourceRecordId = "call-42",
        Channel = Channel.Chat,
        StartedOn = Build.T0,
        EndedOn = Build.T0.AddMinutes(5),
        Customer = new CustomerIdentity(Email: "Jane.Doe@Example.com", Phone: "+49 151 2345 6789"),
        Summary = "Call me on 0151 23456789 or jane.doe@example.com about ORD-4455667",
        IssueText = "reach me at 0151-23456789, ORD-4455667 missing",
    };

    [Fact]
    public void Normalized_interaction_carries_no_raw_email_or_phone()
    {
        var n = InteractionNormalizer.Normalize(Raw());
        Assert.Null(n.Customer.Email);
        Assert.Null(n.Customer.Phone);
        Assert.StartsWith("email:", n.Customer.ResolveKey().Key);
        Assert.DoesNotContain("jane.doe", n.Summary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("23456789", n.Summary);
        Assert.DoesNotContain("23456789", n.IssueText);
        Assert.Contains("ORD-4455667", n.Summary);          // business references survive redaction
    }

    [Fact]
    public void Normalizing_twice_changes_nothing()
    {
        // The publisher normalises before queueing and the processor normalises again.
        var once = InteractionNormalizer.Normalize(Raw());
        var twice = InteractionNormalizer.Normalize(once);
        Assert.Equal(once.Id, twice.Id);
        Assert.Equal(once.Customer.ResolveKey(), twice.Customer.ResolveKey());
        Assert.Equal(once.Summary, twice.Summary);
        Assert.Equal(once.IssueText, twice.IssueText);
    }
}
