using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Vrl.Core.Model;
using Vrl.Core.Processing;

namespace Vrl.Functions;

/// <summary>
/// Publishes ledger commands to the session-enabled queue with SessionId = customer key.
/// Sessions give ordered, exclusive processing per customer while different customers scale out in parallel,
/// which removes read-modify-write races on the customer's ledger without any distributed locks.
/// </summary>
public sealed class CommandPublisher(ServiceBusSender sender, ServiceBusClient client)
{
    private readonly ServiceBusSender _events = client.CreateSender("vrl-dataverse-events");

    /// <summary>Re-schedules a raw Dataverse event onto the events queue, marked so it is processed (not deferred) next time.</summary>
    public Task DeferDataverseEventAsync(BinaryData body, TimeSpan delay, CancellationToken ct)
    {
        var msg = new ServiceBusMessage(body) { ContentType = "application/json", Subject = "deferred" };
        msg.ApplicationProperties["vrl-deferred"] = true;
        return _events.ScheduleMessageAsync(msg, DateTimeOffset.UtcNow.Add(delay), ct);
    }

    /// <param name="revision">
    /// Distinguishes a deliberate re-publish of the same conversation (e.g. "topic" when a Copilot Studio transcript
    /// arrives later) from a duplicate delivery, which the queue's duplicate detection must still drop.
    /// </param>
    public Task PublishIngestAsync(Interaction interaction, CancellationToken ct, string? revision = null)
    {
        // Normalise before publishing: the message then carries only the hashed customer key and redacted text, never
        // raw phone numbers or e-mail addresses (queues keep messages up to 7 days and dead-letter them). The processor
        // normalises again, which is idempotent.
        var normalized = InteractionNormalizer.Normalize(interaction);
        var key = normalized.Customer.ResolveKey().Key ?? $"anon:{normalized.Id:N}";
        var cmd = new LedgerCommand { Type = LedgerCommand.Ingest, Interaction = normalized };
        var messageId = $"{normalized.Id:N}-{interaction.EndedOn.UtcTicks}" + (revision is null ? "" : $"-{revision}");
        return SendAsync(cmd, key, messageId, ct);
    }

    public Task PublishEvaluateAsync(string customerKey, DateTimeOffset asOf, CancellationToken ct) =>
        SendAsync(new LedgerCommand { Type = LedgerCommand.Evaluate, CustomerKey = customerKey }, customerKey,
            $"eval-{customerKey}-{asOf:yyyyMMddHHmm}", ct);

    private Task SendAsync(LedgerCommand cmd, string sessionId, string messageId, CancellationToken ct)
    {
        var msg = new ServiceBusMessage(BinaryData.FromString(JsonSerializer.Serialize(cmd, LedgerCommand.Json)))
        {
            SessionId = sessionId,
            // With duplicate detection enabled on the queue, replays inside the detection window are dropped.
            MessageId = messageId.Length > 128 ? messageId[..128] : messageId,
            ContentType = "application/json",
            Subject = cmd.Type,
        };
        return sender.SendMessageAsync(msg, ct);
    }
}
