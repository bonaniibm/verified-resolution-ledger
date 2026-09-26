using Vrl.Core.Model;

namespace Vrl.Core.Tests;

internal static class Build
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    public static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static int _seq;

    public static Interaction Contact(
        double startHours,
        Guid? contact = null,
        HandlingMode mode = HandlingMode.HumanOnly,
        NativeBotOutcome outcome = NativeBotOutcome.None,
        string? intent = "billing.refund",
        string? summary = null,
        Channel channel = Channel.Chat,
        bool followUp = false,
        Guid? caseId = null,
        double durationMinutes = 10,
        string? phone = null,
        string? topic = null,
        string? queue = null,
        double handleMinutes = 0,
        double credits = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            SourceSystem = "test",
            SourceRecordId = $"rec-{Interlocked.Increment(ref _seq)}",
            Channel = channel,
            HandlingMode = mode,
            NativeBotOutcome = outcome,
            StartedOn = T0.AddHours(startHours),
            EndedOn = T0.AddHours(startHours).AddMinutes(durationMinutes),
            Customer = new CustomerIdentity(ContactId: contact, Phone: phone),
            IntentCode = intent,
            Summary = summary,
            FollowUpScheduled = followUp,
            CaseId = caseId,
            BotTopic = topic,
            QueueName = queue,
            HandleMinutes = handleMinutes,
            AiCredits = credits,
        };
}
