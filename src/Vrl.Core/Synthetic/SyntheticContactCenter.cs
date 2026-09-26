using Vrl.Core.Model;

namespace Vrl.Core.Synthetic;

/// <summary>Known truth for a generated interaction, used to measure the engine's precision/recall.</summary>
public sealed record GroundTruth(
    Guid InteractionId,
    string IssueId,
    bool IssueActuallyResolvedHere,
    bool FollowedBySameIssueContact,
    TimeSpan? GapToNextContact = null,
    bool IdentityLinkable = true);

public sealed record SyntheticDataset(IReadOnlyList<Interaction> Interactions, IReadOnlyDictionary<Guid, GroundTruth> Truth);

public sealed record IssueTemplate(
    string Intent,
    string BotTopic,
    string Queue,
    double BotTrueFixRate,
    double HumanFixRate,
    string[] Summaries,
    string ReferencePrefix);

public sealed class SyntheticOptions
{
    public int Customers { get; init; } = 600;
    public int Days { get; init; } = 30;
    public int Seed { get; init; } = 42;
    public DateTimeOffset End { get; init; } = DateTimeOffset.UtcNow;
    public double BotFirstShare { get; init; } = 0.7;
    public double KnownContactShare { get; init; } = 0.80;
    public double PhoneOnlyShare { get; init; } = 0.12;
    /// <summary>Probability a bot that did NOT fix the issue still reports it as (implied) resolved rather than escalating.</summary>
    public double BotFalseClaimRate { get; init; } = 0.45;
    public double BotAbandonRate { get; init; } = 0.25;
    public double ReturnAfterAbandonRate { get; init; } = 0.6;
}

/// <summary>
/// Generates a realistic, fully-labelled contact-centre history: bot-first journeys, false containment,
/// escalations, channel switching on repeats, unrelated contacts from the same customer, partial identity.
/// Deterministic per seed. Summaries use templates + references so both lexical and semantic signals are exercised.
/// Names and numbers are fictitious.
/// </summary>
public sealed class SyntheticContactCenter(SyntheticOptions options)
{
    public static IReadOnlyList<IssueTemplate> Catalog { get; } =
    [
        new("billing.refund.status", "Refund status", "Billing", 0.55, 0.85,
            ["Customer asking when refund for order {ref} will arrive, returned item two weeks ago",
             "Refund for {ref} still not received after return was accepted",
             "Where is my money back for returned order {ref}, refund not showing on card"], "ORD-"),
        new("billing.invoice.dispute", "Invoice question", "Billing", 0.30, 0.75,
            ["Invoice {ref} shows a charge the customer does not recognise",
             "Customer disputes extra fee on invoice {ref}",
             "Wrong amount billed on invoice {ref}, wants correction"], "INV-"),
        new("delivery.tracking", "Track my order", "Logistics", 0.85, 0.90,
            ["Customer wants tracking update for parcel {ref}",
             "Where is my delivery {ref}, tracking has not updated",
             "Parcel {ref} tracking stuck, asking for delivery date"], "TRK"),
        new("delivery.damaged", "Report damaged item", "Logistics", 0.35, 0.80,
            ["Item in order {ref} arrived damaged, customer wants replacement",
             "Received broken product in delivery {ref}",
             "Damaged goods in order {ref}, asking how to get a replacement"], "ORD-"),
        new("account.password.reset", "Reset password", "Digital Support", 0.80, 0.95,
            ["Customer cannot log in and needs password reset",
             "Locked out of online account, password reset email not arriving",
             "Password reset link expired, still unable to sign in"], ""),
        new("service.appointment.reschedule", "Reschedule appointment", "Service", 0.65, 0.90,
            ["Customer wants to move service appointment {ref} to next week",
             "Reschedule workshop booking {ref}, cannot make the current slot",
             "Change date of service appointment {ref}"], "APT-"),
        new("product.warranty.claim", "Warranty", "Service", 0.20, 0.70,
            ["Customer asking whether fault on product {ref} is covered by warranty",
             "Warranty claim for defective device {ref}",
             "Device {ref} stopped working, wants to know warranty coverage and repair options"], "SN"),
    ];

    public SyntheticDataset Generate()
    {
        var rng = new Random(options.Seed);
        var start = options.End.AddDays(-options.Days);
        var interactions = new List<Interaction>();
        var truth = new Dictionary<Guid, GroundTruth>();
        var seq = 0;

        for (var c = 0; c < options.Customers; c++)
        {
            var identity = MakeIdentity(rng, c);
            var issues = 1 + (rng.NextDouble() < 0.35 ? 1 : 0) + (rng.NextDouble() < 0.10 ? 1 : 0);

            for (var n = 0; n < issues; n++)
            {
                var tpl = Catalog[rng.Next(Catalog.Count)];
                var issueId = $"C{c:D4}-I{n}";
                var reference = tpl.ReferencePrefix.Length == 0 ? "" : $"{tpl.ReferencePrefix}{rng.Next(100000, 999999)}";
                var t = start.AddMinutes(rng.Next(0, options.Days * 24 * 60));
                if (t > options.End.AddHours(-1)) continue;

                var journey = new List<(Interaction I, bool Fixed)>();
                var resolved = false;
                var attempt = 0;
                var botFirst = rng.NextDouble() < options.BotFirstShare;

                while (!resolved && attempt < 4 && t < options.End)
                {
                    var channel = PickChannel(rng, attempt, botFirst);
                    var useBot = attempt == 0 ? botFirst && channel != Channel.Email : rng.NextDouble() < 0.25 && channel != Channel.Email;
                    var summary = Summary(rng, tpl, reference);
                    Interaction interaction;
                    bool fixedHere;
                    var giveUp = false;

                    if (useBot)
                    {
                        var botFixes = rng.NextDouble() < tpl.BotTrueFixRate;
                        var botMin = 2 + rng.NextDouble() * 6;
                        if (botFixes)
                        {
                            fixedHere = true;
                            interaction = Make(ref seq, channel, HandlingMode.BotOnly,
                                rng.NextDouble() < 0.5 ? NativeBotOutcome.Resolved : NativeBotOutcome.ResolvedImplied,
                                t, botMin, 0, identity, tpl, summary, botMin);
                        }
                        else
                        {
                            var roll = rng.NextDouble();
                            if (roll < options.BotAbandonRate)
                            {
                                fixedHere = false;
                                giveUp = rng.NextDouble() > options.ReturnAfterAbandonRate;
                                interaction = Make(ref seq, channel, HandlingMode.BotOnly, NativeBotOutcome.Abandoned,
                                    t, botMin, 0, identity, tpl, summary, botMin);
                            }
                            else if (roll < options.BotAbandonRate + options.BotFalseClaimRate * (1 - options.BotAbandonRate))
                            {
                                fixedHere = false; // false containment
                                interaction = Make(ref seq, channel, HandlingMode.BotOnly,
                                    rng.NextDouble() < 0.7 ? NativeBotOutcome.ResolvedImplied : NativeBotOutcome.Resolved,
                                    t, botMin, 0, identity, tpl, summary, botMin);
                            }
                            else
                            {
                                var handle = 6 + rng.NextDouble() * 14;
                                fixedHere = rng.NextDouble() < tpl.HumanFixRate;
                                interaction = Make(ref seq, channel, HandlingMode.BotThenHuman, NativeBotOutcome.Escalated,
                                    t, botMin + handle, handle, identity, tpl, summary, botMin);
                            }
                        }
                    }
                    else
                    {
                        var handle = channel == Channel.Email ? 8 + rng.NextDouble() * 10 : 5 + rng.NextDouble() * 15;
                        fixedHere = rng.NextDouble() < tpl.HumanFixRate;
                        interaction = Make(ref seq, channel, HandlingMode.HumanOnly, NativeBotOutcome.None,
                            t, handle, handle, identity, tpl, summary, 0);
                        // A small share of genuine callbacks were scheduled on purpose.
                        if (!fixedHere && rng.NextDouble() < 0.15) interaction = interaction with { FollowUpScheduled = true };
                    }

                    // Intent classification is imperfect: sometimes missing, sometimes only the family is right.
                    var noise = rng.NextDouble();
                    if (noise < 0.12) interaction = interaction with { IntentCode = null };
                    else if (noise < 0.22) interaction = interaction with { IntentCode = tpl.Intent.Split('.')[0] + ".general" };

                    journey.Add((interaction, fixedHere));
                    resolved = fixedHere;
                    if (giveUp) break;
                    attempt++;
                    // Repeat gap: most repeats come within 1–48h, some later.
                    t = interaction.EndedOn.AddHours(rng.NextDouble() < 0.75 ? 0.5 + rng.NextDouble() * 47 : 48 + rng.NextDouble() * 60);
                }

                for (var k = 0; k < journey.Count; k++)
                {
                    var (i, fx) = journey[k];
                    interactions.Add(i);
                    var gap = k < journey.Count - 1 ? journey[k + 1].I.StartedOn - i.EndedOn : (TimeSpan?)null;
                    truth[i.Id] = new GroundTruth(i.Id, issueId, fx, k < journey.Count - 1, gap,
                        i.Customer.ResolveKey().Confidence >= IdentityConfidence.Medium);
                }
            }
        }

        return new SyntheticDataset(interactions.OrderBy(i => i.StartedOn).ToList(), truth);
    }

    private CustomerIdentity MakeIdentity(Random rng, int index)
    {
        var roll = rng.NextDouble();
        var contactId = Util.DeterministicGuid.Create(Util.DeterministicGuid.LedgerNamespace, $"synthetic-contact-{options.Seed}-{index}");
        if (roll < options.KnownContactShare) return new CustomerIdentity(ContactId: contactId);
        if (roll < options.KnownContactShare + options.PhoneOnlyShare)
            return new CustomerIdentity(Phone: $"+49 151 {index:D4}{rng.Next(1000, 9999)}");
        return CustomerIdentity.Anonymous;
    }

    private static Channel PickChannel(Random rng, int attempt, bool botFirst)
    {
        var r = rng.NextDouble();
        // Repeat contacts skew to voice – customers escalate the channel when self-service failed.
        if (attempt > 0) return r < 0.6 ? Channel.Voice : r < 0.85 ? Channel.Chat : Channel.Email;
        if (botFirst) return r < 0.55 ? Channel.Chat : r < 0.85 ? Channel.Voice : Channel.Sms;
        return r < 0.4 ? Channel.Voice : r < 0.7 ? Channel.Chat : Channel.Email;
    }

    private static string Summary(Random rng, IssueTemplate tpl, string reference)
    {
        var s = tpl.Summaries[rng.Next(tpl.Summaries.Length)];
        // Customers don't always quote their reference number.
        var r = reference.Length > 0 && rng.NextDouble() < 0.6 ? reference : "their order";
        return s.Replace("{ref}", r);
    }

    private static Interaction Make(
        ref int seq, Channel channel, HandlingMode mode, NativeBotOutcome outcome, DateTimeOffset start,
        double durationMin, double handleMin, CustomerIdentity customer, IssueTemplate tpl, string summary, double botMin)
    {
        seq++;
        var recordId = $"SYN-{seq:D6}";
        var credits = botMin <= 0 ? 0 : channel == Channel.Voice ? Math.Round(botMin * 35, 1) : 6 + (seq % 5) * 2;
        return new Interaction
        {
            Id = Util.DeterministicGuid.ForInteraction("synthetic", recordId),
            SourceSystem = "synthetic",
            SourceRecordId = recordId,
            Channel = channel,
            HandlingMode = mode,
            NativeBotOutcome = outcome,
            StartedOn = start,
            EndedOn = start.AddMinutes(durationMin),
            Customer = customer,
            IntentCode = tpl.Intent,
            Summary = summary,
            QueueName = mode is HandlingMode.HumanOnly or HandlingMode.BotThenHuman ? tpl.Queue : null,
            BotTopic = mode is HandlingMode.BotOnly or HandlingMode.BotThenHuman ? tpl.BotTopic : null,
            BotMinutes = Math.Round(botMin, 2),
            HandleMinutes = Math.Round(handleMin, 2),
            TelephonyMinutes = channel == Channel.Voice ? Math.Round(durationMin, 2) : 0,
            AiCredits = credits,
        };
    }
}
