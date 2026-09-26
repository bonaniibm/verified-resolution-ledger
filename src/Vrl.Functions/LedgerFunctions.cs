using System.Net;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vrl.Core.Abstractions;
using Vrl.Core.Model;
using Vrl.Core.Processing;
using Vrl.Dataverse.Adapters;

namespace Vrl.Functions;

/// <summary>
/// Pipeline:
///   Dataverse service endpoint ──► [vrl-dataverse-events] ──► DataverseEventRouter ─┐
///   External adapters (HTTP) ──► PostInteraction ──────────────────────────────────┼─► [vrl-interactions, sessions by customer] ──► ProcessLedgerCommand
///   Timer ──► MatureVerdicts (finds customers with closed windows) ─────────────────┤
///   Timer ──► EnrichBotTopics (late Copilot Studio transcripts) ──────────────────────┘
/// </summary>
public sealed class LedgerFunctions(
    LedgerProcessor processor,
    ILedgerRepository repository,
    OmnichannelConversationAdapter omnichannel,
    BotTopicEnricher botTopics,
    CommandPublisher publisher,
    IOptions<LedgerOptions> options,
    ILogger<LedgerFunctions> logger)
{
    /// <summary>
    /// Receives Dataverse RemoteExecutionContext (JSON message format) for msdyn_ocliveworkitem updates,
    /// maps the closed conversation to the canonical model and routes it to the per-customer session queue.
    /// </summary>
    [Function(nameof(DataverseEventRouter))]
    public async Task DataverseEventRouter(
        [ServiceBusTrigger("vrl-dataverse-events", Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message.Body.ToMemory());
        var root = doc.RootElement;
        var entity = root.TryGetProperty("PrimaryEntityName", out var n) ? n.GetString() : null;
        var id = root.TryGetProperty("PrimaryEntityId", out var i) && i.TryGetGuid(out var g) ? g : Guid.Empty;

        if (entity != OmnichannelConversationAdapter.Table || id == Guid.Empty)
        {
            logger.LogInformation("Ignoring event for {Entity}", entity);
            return;
        }

        // Omnichannel writes the transcript around conversation close. Defer mapping once by ~90s so the
        // transcript text (used for same-issue similarity) is available. Scheduled messages, not sleeps.
        if (!message.ApplicationProperties.ContainsKey("vrl-deferred"))
        {
            await publisher.DeferDataverseEventAsync(message.Body, TimeSpan.FromSeconds(90), ct);
            logger.LogInformation("Deferred mapping of conversation {Id} by 90s", id);
            return;
        }

        var interaction = await omnichannel.BuildAsync(id, ct);
        if (interaction is null) return;
        await publisher.PublishIngestAsync(interaction, ct);
    }

    /// <summary>Single writer per customer: sessions guarantee ordered, exclusive processing of a customer's ledger.</summary>
    [Function(nameof(ProcessLedgerCommand))]
    public async Task ProcessLedgerCommand(
        [ServiceBusTrigger("vrl-interactions", Connection = "ServiceBusConnection", IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        CancellationToken ct)
    {
        var cmd = JsonSerializer.Deserialize<LedgerCommand>(message.Body.ToString(), LedgerCommand.Json)
                  ?? throw new InvalidOperationException("Empty ledger command.");

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CustomerSession"] = message.SessionId });

        switch (cmd.Type)
        {
            case LedgerCommand.Ingest when cmd.Interaction is not null:
                var outcome = await processor.IngestAsync(cmd.Interaction, ct);
                logger.LogInformation("Ingested {InteractionId}; history {History}", outcome.InteractionId, outcome.HistorySize);
                break;
            case LedgerCommand.Evaluate when !string.IsNullOrWhiteSpace(cmd.CustomerKey):
                await processor.EvaluateCustomerAsync(cmd.CustomerKey, ct);
                break;
            default:
                // Poison message: throwing lets Service Bus retry then dead-letter for investigation.
                throw new InvalidOperationException($"Unsupported ledger command '{cmd.Type}'.");
        }
    }

    /// <summary>
    /// Every 15 minutes: find customers whose repeat windows have closed and queue a re-evaluation into their session,
    /// so maturation never races with live ingestion for the same customer.
    /// </summary>
    [Function(nameof(MatureVerdicts))]
    public async Task MatureVerdicts([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var keys = await repository.FindCustomerKeysDueForMaturationAsync(now, options.Value.MaturationBatch, ct);
        foreach (var key in keys) await publisher.PublishEvaluateAsync(key, now, ct);
        logger.LogInformation("Queued maturation for {Count} customer(s)", keys.Count);
    }

    /// <summary>
    /// Every 15 minutes (offset from maturation): re-maps recent AI-agent conversations whose Copilot Studio transcript
    /// has arrived since ingestion, so root-cause reporting can name the topic. Re-ingestion is an idempotent upsert.
    /// </summary>
    [Function(nameof(EnrichBotTopics))]
    public async Task EnrichBotTopics([TimerTrigger("0 7-59/15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        foreach (var interaction in await botTopics.FindEnrichableAsync(DateTimeOffset.UtcNow, 200, ct))
            await publisher.PublishIngestAsync(interaction, ct, revision: "topic");
    }

    /// <summary>
    /// Ingestion API for non-Dataverse sources (embedded Salesforce/ServiceNow, IVR vendors, synthetic load tests).
    /// Protected by a function key; front with APIM + Entra ID for production.
    /// </summary>
    [Function(nameof(PostInteraction))]
    public async Task<IActionResult> PostInteraction(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "interactions")] HttpRequest req,
        CancellationToken ct)
    {
        Interaction? interaction;
        try
        {
            interaction = await JsonSerializer.DeserializeAsync<Interaction>(req.Body, LedgerCommand.Json, ct);
        }
        catch (JsonException ex)
        {
            return new BadRequestObjectResult(new { error = "Invalid JSON", detail = ex.Message });
        }

        if (interaction is null || string.IsNullOrWhiteSpace(interaction.SourceSystem) || string.IsNullOrWhiteSpace(interaction.SourceRecordId))
            return new BadRequestObjectResult(new { error = "sourceSystem and sourceRecordId are required" });
        if (interaction.Customer is null)
            return new BadRequestObjectResult(new { error = "customer is required (use {} for an anonymous contact)" });
        if (interaction.EndedOn < interaction.StartedOn)
            return new BadRequestObjectResult(new { error = "endedOn must be on/after startedOn" });

        // Never trust client-supplied ledger state or a pre-resolved identity: a caller must not be able to claim a
        // customer key (e.g. contact:<id> at High confidence) or inject vectors. Identity is derived from the raw fields.
        await publisher.PublishIngestAsync(interaction with
        {
            Id = Guid.Empty,
            Persisted = null,
            ExistingEpisodeKey = null,
            Embedding = null,
            EmbeddingModel = null,
            Customer = interaction.Customer with { KnownKey = null, KnownConfidence = IdentityConfidence.None },
        }, ct);
        return new ObjectResult(new { accepted = true }) { StatusCode = (int)HttpStatusCode.Accepted };
    }
}
