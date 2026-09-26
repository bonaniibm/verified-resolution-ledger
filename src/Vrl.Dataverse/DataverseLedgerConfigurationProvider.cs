using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vrl.Core.Abstractions;
using Vrl.Core.Costing;
using Vrl.Core.Model;
using Vrl.Core.Policy;
using S = Vrl.Dataverse.Schema.LedgerSchema;

namespace Vrl.Dataverse;

/// <summary>
/// Reads business-owned policy (bpc_windowpolicy) and Finance-owned rates (bpc_costrate) with a short cache,
/// so changes made in the model-driven app take effect within minutes without a redeploy.
/// Falls back to engine defaults for anything not configured.
/// </summary>
public sealed class DataverseLedgerConfigurationProvider(
    IOrganizationServiceAsync2 service,
    ILogger<DataverseLedgerConfigurationProvider> logger,
    string? embeddingModelId = null,
    TimeSpan? cacheDuration = null) : ILedgerConfigurationProvider
{
    private readonly TimeSpan _ttl = cacheDuration ?? TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (LedgerPolicy, CostRateCard)? _cached;
    private DateTimeOffset _loadedAt;

    public async Task<(LedgerPolicy Policy, CostRateCard Rates)> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } c && DateTimeOffset.UtcNow - _loadedAt < _ttl) return c;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is { } c2 && DateTimeOffset.UtcNow - _loadedAt < _ttl) return c2;
            var loaded = (await LoadPolicyAsync(ct), await LoadRatesAsync(ct));
            _cached = loaded;
            _loadedAt = DateTimeOffset.UtcNow;
            return loaded;
        }
        catch (Exception ex) when (_cached is not null && ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Configuration refresh failed; using last known configuration");
            return _cached.Value;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<LedgerPolicy> LoadPolicyAsync(CancellationToken ct)
    {
        var rows = await Active(S.WindowPolicy.Table,
            [S.WindowPolicy.Scope, S.WindowPolicy.Channel, S.WindowPolicy.IntentFamily, S.WindowPolicy.WindowHours, S.WindowPolicy.SameIssueThreshold], ct);

        var defaults = LedgerPolicy.Default;
        var window = defaults.DefaultWindow;
        var threshold = defaults.SameIssueThreshold;
        var channels = new Dictionary<Channel, TimeSpan>(defaults.ChannelWindows);
        var intents = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            var hours = r.GetAttributeValue<int?>(S.WindowPolicy.WindowHours);
            var scope = (S.PolicyScope?)r.GetAttributeValue<OptionSetValue>(S.WindowPolicy.Scope)?.Value;
            switch (scope)
            {
                case S.PolicyScope.Global:
                    if (hours > 0) window = TimeSpan.FromHours(hours.Value);
                    if (r.GetAttributeValue<decimal?>(S.WindowPolicy.SameIssueThreshold) is { } t and > 0 and <= 1) threshold = (double)t;
                    break;
                case S.PolicyScope.Channel when hours > 0 && r.GetAttributeValue<OptionSetValue>(S.WindowPolicy.Channel) is { } ch:
                    channels[(Channel)ch.Value] = TimeSpan.FromHours(hours.Value);
                    break;
                case S.PolicyScope.IntentFamily when hours > 0 && r.GetAttributeValue<string>(S.WindowPolicy.IntentFamily) is { Length: > 0 } fam:
                    intents[fam.Trim()] = TimeSpan.FromHours(hours.Value);
                    break;
            }
        }

        return new LedgerPolicy
        {
            DefaultWindow = window,
            SameIssueThreshold = threshold,
            ChannelWindows = channels,
            IntentFamilyWindows = intents,
            Weights = SimilarityWeights.ForEmbeddingModel(embeddingModelId),
        };
    }

    private async Task<CostRateCard> LoadRatesAsync(CancellationToken ct)
    {
        var rows = await Active(S.CostRate.Table,
            [S.CostRate.RateType, S.CostRate.Channel, S.CostRate.QueueName, S.CostRate.Amount, S.CostRate.Currency], ct);

        var d = CostRateCard.Default;
        decimal credit = d.CreditPrice, tel = d.TelephonyPerMinute, labor = d.LaborPerHour;
        var currency = d.Currency;
        var queues = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var channels = new Dictionary<Channel, decimal>();

        foreach (var r in rows)
        {
            var amount = r.GetAttributeValue<decimal?>(S.CostRate.Amount);
            if (amount is null or < 0) continue;
            if (r.GetAttributeValue<string>(S.CostRate.Currency) is { Length: 3 } cur) currency = cur.ToUpperInvariant();
            var queue = r.GetAttributeValue<string>(S.CostRate.QueueName);

            switch ((S.RateType?)r.GetAttributeValue<OptionSetValue>(S.CostRate.RateType)?.Value)
            {
                case S.RateType.CreditPrice: credit = amount.Value; break;
                case S.RateType.TelephonyPerMinute: tel = amount.Value; break;
                case S.RateType.LaborPerHour when !string.IsNullOrWhiteSpace(queue): queues[queue.Trim()] = amount.Value; break;
                case S.RateType.LaborPerHour: labor = amount.Value; break;
                case S.RateType.ChannelPerMinute when r.GetAttributeValue<OptionSetValue>(S.CostRate.Channel) is { } ch:
                    channels[(Channel)ch.Value] = amount.Value; break;
            }
        }

        return new CostRateCard
        {
            Currency = currency, CreditPrice = credit, TelephonyPerMinute = tel, LaborPerHour = labor,
            QueueLaborPerHour = queues, ChannelPerMinute = channels,
        };
    }

    private async Task<IReadOnlyList<Entity>> Active(string table, string[] columns, CancellationToken ct)
    {
        var q = new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(columns),
            Criteria = { Conditions = { new ConditionExpression("statecode", ConditionOperator.Equal, 0) } },
            Orders = { new OrderExpression("modifiedon", OrderType.Ascending) }, // later edits win
        };
        return (await service.RetrieveMultipleAsync(q, ct)).Entities;
    }
}
