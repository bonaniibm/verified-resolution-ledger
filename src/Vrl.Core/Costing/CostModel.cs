using Vrl.Core.Model;

namespace Vrl.Core.Costing;

/// <summary>
/// Rate card used to price every interaction. Persisted in Dataverse (bpc_costrate) so Finance owns the numbers.
/// Defaults are placeholders, not Microsoft list prices – set them from your own contract.
/// </summary>
public sealed class CostRateCard
{
    public string Currency { get; init; } = "EUR";

    /// <summary>Price of one Copilot Studio / Copilot credit.</summary>
    public decimal CreditPrice { get; init; } = 0.01m;

    /// <summary>Default telephony (ACS PSTN / direct routing) cost per minute for voice interactions.</summary>
    public decimal TelephonyPerMinute { get; init; } = 0.02m;

    /// <summary>Fully-loaded human cost per hour when no queue-specific rate exists.</summary>
    public decimal LaborPerHour { get; init; } = 35m;

    /// <summary>Queue-specific loaded labor rates (e.g. tier-2 or specialist queues).</summary>
    public IReadOnlyDictionary<string, decimal> QueueLaborPerHour { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Channel-specific telephony/messaging rates per minute (e.g. SMS carrier fees can be modelled per minute of session).</summary>
    public IReadOnlyDictionary<Channel, decimal> ChannelPerMinute { get; init; } = new Dictionary<Channel, decimal>();

    public static CostRateCard Default { get; } = new();
}

public sealed record InteractionCost(decimal Ai, decimal Telephony, decimal Labor)
{
    public decimal Total => Ai + Telephony + Labor;
}

public static class CostCalculator
{
    public static InteractionCost Price(Interaction i, CostRateCard rates)
    {
        var ai = (decimal)i.AiCredits * rates.CreditPrice;

        var perMinute = rates.ChannelPerMinute.TryGetValue(i.Channel, out var r)
            ? r
            : i.Channel == Channel.Voice ? rates.TelephonyPerMinute : 0m;
        var telephony = (decimal)i.TelephonyMinutes * perMinute;

        var laborRate = i.QueueName is not null && rates.QueueLaborPerHour.TryGetValue(i.QueueName, out var q)
            ? q
            : rates.LaborPerHour;
        var labor = (decimal)i.HandleMinutes / 60m * laborRate;

        return new InteractionCost(Round(ai), Round(telephony), Round(labor));
    }

    private static decimal Round(decimal d) => Math.Round(d, 4, MidpointRounding.AwayFromZero);
}
