using Vrl.Dataverse.Adapters;

namespace Vrl.Dataverse.Tests;

public class TranscriptExtractorTests
{
    // Real Omnichannel transcripts (msdyn_transcript note bodies, anonymised): stored newest-first, messages nested as a
    // JSON string, senders marked with isFromAgent / tags.
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Bot_chat_full_text_is_in_conversation_order_without_control_messages()
    {
        var text = TranscriptExtractor.Extract(Fixture("omnichannel-transcript-bot.json"))!;
        Assert.StartsWith("Hello, I'm VRL Parcel Bot. How can I help?", text);
        Assert.EndsWith("You're welcome.", text);
        Assert.DoesNotContain("<addmember>", text);
        Assert.DoesNotContain("deletemember", text);
    }

    [Fact]
    public void Bot_chat_customer_text_excludes_the_bot()
    {
        var issue = TranscriptExtractor.ExtractCustomer(Fixture("omnichannel-transcript-bot.json"));
        Assert.Equal("My parcel TRK‑55667788 shows delivered but I never received it. OK thanks", issue);
    }

    [Fact]
    public void Human_chat_customer_text_excludes_representative_and_system_notices()
    {
        var json = Fixture("omnichannel-transcript-human.json");
        Assert.Equal("My parcel TRK-55667788 still hasn't arrived, the bot told me to wait.", TranscriptExtractor.ExtractCustomer(json));
        var full = TranscriptExtractor.Extract(json)!;
        Assert.Contains("Please wait for 3 days", full);                 // the representative's reply stays for display
        Assert.DoesNotContain("Customer has ended the conversation", full); // system notice
    }

    [Fact]
    public void Transcripts_without_sender_information_yield_no_customer_text()
    {
        const string json = """[{"content":"hello"},{"content":"world"}]""";
        Assert.Null(TranscriptExtractor.ExtractCustomer(json));
        Assert.NotNull(TranscriptExtractor.Extract(json));
    }
}
