using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Query;
using Vrl.Core.Model;
using S = Vrl.Dataverse.Schema.LedgerSchema;

namespace Vrl.Dataverse.Adapters;

/// <summary>
/// Copilot Studio writes its transcript when the bot session ends, typically ~30 minutes after the conversation
/// started, so bot conversations are usually ingested without a topic. This finds recent bot conversations still
/// missing a topic and re-maps them; callers re-ingest the ones that now have one (idempotent upsert).
/// </summary>
public sealed class BotTopicEnricher(
    IOrganizationServiceAsync2 service,
    OmnichannelConversationAdapter adapter,
    ILogger<BotTopicEnricher> logger)
{
    /// <summary>How far back to keep looking for a late transcript.</summary>
    public TimeSpan LookBack { get; init; } = TimeSpan.FromHours(6);

    public async Task<IReadOnlyList<Interaction>> FindEnrichableAsync(DateTimeOffset now, int max, CancellationToken ct)
    {
        var q = new QueryExpression(S.Interaction.Table)
        {
            ColumnSet = new ColumnSet(S.Interaction.SourceRecordId),
            TopCount = max,
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression(S.Interaction.SourceSystem, ConditionOperator.Equal, OmnichannelConversationAdapter.SourceSystem),
                    new ConditionExpression(S.Interaction.HandlingMode, ConditionOperator.In, (int)HandlingMode.BotOnly, (int)HandlingMode.BotThenHuman),
                    new ConditionExpression(S.Interaction.BotTopic, ConditionOperator.Null),
                    new ConditionExpression(S.Interaction.StartedOn, ConditionOperator.GreaterEqual, now.Add(-LookBack).UtcDateTime),
                },
            },
        };
        var ids = (await service.RetrieveMultipleAsync(q, ct)).Entities
            .Select(e => e.GetAttributeValue<string>(S.Interaction.SourceRecordId))
            .Where(s => Guid.TryParse(s, out _)).Select(Guid.Parse).ToList();

        var enriched = new List<Interaction>();
        foreach (var id in ids)
        {
            var interaction = await adapter.BuildAsync(id, ct);
            if (interaction?.BotTopic is not null) enriched.Add(interaction);
        }
        logger.LogInformation("Bot topic enrichment: {Candidates} candidate(s), {Enriched} with a topic now", ids.Count, enriched.Count);
        return enriched;
    }
}
