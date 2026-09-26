using Vrl.Dataverse.Adapters;

namespace Vrl.Dataverse.Tests;

public class CopilotStudioTranscriptParserTests
{
    // Real transcript of a Copilot Studio agent connected to Dynamics 365 Contact Center (ids and e-mail anonymised).
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Real_transcript_yields_conversation_business_topic_and_copilot_outcome()
    {
        var info = CopilotStudioTranscriptParser.Parse(Fixture("copilot-transcript-parcel.json"));

        Assert.NotNull(info);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), info!.ConversationId);
        Assert.Equal(new[] { "Parcel not received", "Thank you" }, info.Topics);
        Assert.Equal("Parcel not received", info.Topic);          // "Thank you" is a courtesy topic, not the issue
        Assert.Equal("Abandoned", info.CopilotOutcome);           // what Copilot Studio analytics report …
        Assert.Equal("UserExit", info.OutcomeReason);             // … for a session Contact Center counts as deflected
        Assert.False(info.ImpliedSuccess);
        Assert.Equal(5, info.TurnCount);
    }

    [Fact]
    public void Legacy_topic_traces_are_read()
    {
        const string json = """
            {"activities":[
              {"type":"event","name":"startConversation","value":{"msdyn_liveworkitemid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}},
              {"type":"trace","valueType":"DialogRedirect","value":{"targetDialogId":"auto_agent_X.topic.CheckRefundStatus"}},
              {"type":"trace","valueType":"SessionInfo","value":{"outcome":"Resolved","impliedSuccess":true,"turnCount":3}}]}
            """;
        var info = CopilotStudioTranscriptParser.Parse(json)!;
        Assert.Equal("CheckRefundStatus", info.Topic);
        Assert.True(info.ImpliedSuccess);
        Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), info.ConversationId);
    }

    [Fact]
    public void Only_courtesy_topics_means_no_business_topic()
    {
        const string json = """
            {"activities":[
              {"type":"trace","valueType":"IntentRecognition","value":{"intentTitle":"Greeting"}},
              {"type":"trace","valueType":"IntentRecognition","value":{"intentTitle":"Escalate"}}]}
            """;
        var info = CopilotStudioTranscriptParser.Parse(json)!;
        Assert.Null(info.Topic);
        Assert.Null(info.ConversationId);
        Assert.Equal(2, info.Topics.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"no\":\"activities\"}")]
    public void Unknown_shapes_return_null_without_throwing(string? content) =>
        Assert.Null(CopilotStudioTranscriptParser.Parse(content));
}
