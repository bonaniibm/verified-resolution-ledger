using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vrl.Core.Abstractions;
using Vrl.Core.Engine;
using Vrl.Core.Model;
using Vrl.Core.Similarity;
using I = Vrl.Dataverse.Schema.LedgerSchema.Interaction;
using E = Vrl.Dataverse.Schema.LedgerSchema.Episode;

namespace Vrl.Dataverse;

/// <summary>
/// Dataverse implementation of the ledger store.
///
/// API-limit discipline (service protection limits are per user per 5 minutes):
///  * ingestion is a single Upsert by deterministic primary key;
///  * history is one paged query per customer;
///  * evaluation writes only rows whose verdict/episode/cost changed, inside one ExecuteTransaction per customer,
///    so a customer's ledger is never left half-updated.
/// </summary>
public sealed class DataverseLedgerRepository(
    IOrganizationServiceAsync2 service,
    ILogger<DataverseLedgerRepository> logger,
    string? embeddingModelId = null) : ILedgerRepository
{
    private const int TransactionBatchSize = 200;

    private HashSet<string>? _interactionColumns;
    private readonly SemaphoreSlim _metaLock = new(1, 1);

    public async Task UpsertInteractionAsync(Interaction interaction, CancellationToken ct = default)
    {
        var entity = InteractionMapper.ToSourceEntity(interaction, embeddingModelId);
        await DropUnknownColumnsAsync(entity, ct);
        await service.ExecuteAsync(new UpsertRequest { Target = entity }, ct);
    }

    /// <summary>
    /// Optional columns (e.g. the Case lookup when Customer Service isn't installed) may not exist in every
    /// environment. Strip them rather than failing, using a once-per-instance metadata read.
    /// </summary>
    private async Task DropUnknownColumnsAsync(Entity entity, CancellationToken ct)
    {
        if (_interactionColumns is null)
        {
            await _metaLock.WaitAsync(ct);
            try
            {
                if (_interactionColumns is null)
                {
                    var resp = (RetrieveEntityResponse)await service.ExecuteAsync(new RetrieveEntityRequest
                    {
                        LogicalName = I.Table,
                        EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes,
                    }, ct);
                    _interactionColumns = resp.EntityMetadata.Attributes.Select(a => a.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
            finally
            {
                _metaLock.Release();
            }
        }

        foreach (var attr in entity.Attributes.Keys.Where(k => !_interactionColumns.Contains(k)).ToList())
        {
            logger.LogDebug("Column {Column} not present in environment; skipped", attr);
            entity.Attributes.Remove(attr);
        }
    }

    public async Task<IReadOnlyList<Interaction>> LoadCustomerHistoryAsync(
        string customerKey, DateTimeOffset since, int maxItems, CancellationToken ct = default)
    {
        var probe = new Entity(I.Table);
        foreach (var c in InteractionMapper.SourceColumns.Concat(InteractionMapper.StateColumns)) probe[c] = null;
        await DropUnknownColumnsAsync(probe, ct);

        var query = new QueryExpression(I.Table)
        {
            ColumnSet = new ColumnSet([.. probe.Attributes.Keys]),
            TopCount = maxItems + 1,
            Criteria =
            {
                FilterOperator = LogicalOperator.And,
                Conditions =
                {
                    new ConditionExpression(I.CustomerKey, ConditionOperator.Equal, customerKey),
                    new ConditionExpression("statecode", ConditionOperator.Equal, 0),
                },
                Filters =
                {
                    new FilterExpression(LogicalOperator.Or)
                    {
                        Conditions =
                        {
                            new ConditionExpression(I.EndedOn, ConditionOperator.GreaterEqual, since.UtcDateTime),
                            new ConditionExpression(I.VerdictFinal, ConditionOperator.NotEqual, true),
                        },
                    },
                },
            },
            // Most recent first so a capped result keeps the rows that matter; engine re-sorts.
            Orders = { new OrderExpression(I.StartedOn, OrderType.Descending) },
            NoLock = true,
        };

        var result = await service.RetrieveMultipleAsync(query, ct);
        return result.Entities.Select(InteractionMapper.FromEntity).OrderBy(i => i.StartedOn).ToList();
    }

    public async Task SaveEvaluationAsync(
        string? customerKey, IReadOnlyList<Interaction> scope, EvaluationResult result, DateTimeOffset since,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var byId = scope.ToDictionary(i => i.Id);
        var requests = new List<OrganizationRequest>();

        // 1. Episodes first so interaction lookups resolve.
        foreach (var ep in result.Episodes)
        {
            var contact = ep.InteractionIds.Select(id => byId.GetValueOrDefault(id)?.Customer.ContactId).FirstOrDefault(c => c is not null);
            requests.Add(new UpsertRequest { Target = InteractionMapper.ToEpisodeEntity(ep, contact, result.EngineVersion) });
        }

        // 2. Changed interaction verdicts only.
        var changed = 0;
        foreach (var ev in result.Interactions)
        {
            if (InteractionMapper.IsUnchanged(byId.GetValueOrDefault(ev.InteractionId)?.Persisted, ev)) continue;
            requests.Add(new UpdateRequest { Target = InteractionMapper.ToEvaluationUpdate(ev, now, result.EngineVersion) });
            changed++;
        }

        // 3. Episodes that dissolved (e.g. a relink after backfill) – only inside the fully-loaded horizon.
        if (customerKey is not null)
        {
            var keep = result.Episodes.Select(e => e.EpisodeKey).ToHashSet(StringComparer.Ordinal);
            var existing = await service.RetrieveMultipleAsync(new QueryExpression(E.Table)
            {
                ColumnSet = new ColumnSet(E.EpisodeKey),
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression(E.CustomerKey, ConditionOperator.Equal, customerKey),
                        new ConditionExpression(E.LastEndedOn, ConditionOperator.GreaterEqual, since.UtcDateTime),
                    },
                },
            }, ct);
            foreach (var stale in existing.Entities.Where(e => !keep.Contains(e.GetAttributeValue<string>(E.EpisodeKey) ?? "")))
                requests.Add(new DeleteRequest { Target = stale.ToEntityReference() });
        }

        // Episode content is a pure function of member verdicts/keys/costs, so if no interaction changed and nothing
        // dissolved, the episodes are unchanged too – skip all writes (the common case for replays and maturation no-ops).
        if (changed == 0 && !requests.OfType<DeleteRequest>().Any())
        {
            logger.LogDebug("No verdict changes for {CustomerKey}", customerKey);
            return;
        }

        foreach (var chunk in requests.Chunk(TransactionBatchSize))
        {
            var tx = new ExecuteTransactionRequest { Requests = [.. chunk], ReturnResponses = false };
            await service.ExecuteAsync(tx, ct);
        }

        logger.LogInformation(
            "Saved ledger for {CustomerKey}: {Changed} verdict change(s), {Episodes} episode(s), {Requests} request(s)",
            customerKey ?? "(anonymous)", changed, result.Episodes.Count, requests.Count);
    }

    public async Task<IReadOnlyList<string>> FindCustomerKeysDueForMaturationAsync(
        DateTimeOffset asOf, int maxKeys, CancellationToken ct = default)
    {
        var query = new QueryExpression(I.Table)
        {
            ColumnSet = new ColumnSet(I.CustomerKey),
            Distinct = true,
            TopCount = maxKeys,
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression(I.VerdictFinal, ConditionOperator.Equal, false),
                    new ConditionExpression(I.WindowClosesOn, ConditionOperator.LessEqual, asOf.UtcDateTime),
                    new ConditionExpression(I.CustomerKey, ConditionOperator.NotNull),
                    new ConditionExpression("statecode", ConditionOperator.Equal, 0),
                },
            },
            NoLock = true,
        };
        var result = await service.RetrieveMultipleAsync(query, ct);
        return result.Entities.Select(e => e.GetAttributeValue<string>(I.CustomerKey)).Where(k => k is not null).Distinct().ToList()!;
    }
}
