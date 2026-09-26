using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vrl.Core.Abstractions;
using Vrl.Core.Engine;
using Vrl.Core.Model;
using Vrl.Core.Policy;
using Vrl.Core.Similarity;

namespace Vrl.Core.Processing;

public sealed record ProcessingOutcome(
    Guid InteractionId,
    string? CustomerKey,
    int HistorySize,
    int VerdictsChanged,
    bool IdentityTooBroad);

/// <summary>
/// Application service orchestrating ingest → embed → load customer history → evaluate → persist.
/// Host-agnostic: called from Azure Functions (Service Bus / timer), the backfill tool, or tests.
/// Callers must serialise work per customer key (Service Bus sessions do this) to avoid lost updates.
/// </summary>
public sealed class LedgerProcessor
{
    /// <summary>
    /// Above this many interactions in the look-back horizon the identity is almost certainly shared
    /// (switchboard number, generic mailbox, test contact). Linking is skipped to protect accuracy and throughput.
    /// </summary>
    public const int MaxHistoryPerCustomer = 250;

    private readonly ILedgerRepository _repository;
    private readonly ILedgerConfigurationProvider _configuration;
    private readonly IEmbeddingProvider? _embeddings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public LedgerProcessor(
        ILedgerRepository repository,
        ILedgerConfigurationProvider configuration,
        IEmbeddingProvider? embeddings = null,
        TimeProvider? time = null,
        ILogger<LedgerProcessor>? logger = null)
    {
        _repository = repository;
        _configuration = configuration;
        _embeddings = embeddings;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<LedgerProcessor>.Instance;
    }

    /// <summary>Ingests one interaction from any adapter and re-evaluates that customer's open history.</summary>
    public async Task<ProcessingOutcome> IngestAsync(Interaction source, CancellationToken ct = default)
    {
        var interaction = InteractionNormalizer.Normalize(source);

        // Similarity is computed on the customer's own words when the adapter could isolate them.
        var similarityText = interaction.IssueText ?? interaction.Summary;
        if (interaction.Embedding is null && _embeddings is not null && !string.IsNullOrWhiteSpace(similarityText))
        {
            try
            {
                interaction = interaction with
                {
                    Embedding = await _embeddings.EmbedAsync(similarityText!, ct),
                    EmbeddingModel = _embeddings.ModelId,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Embeddings improve recall but are not required; degrade gracefully rather than dead-letter.
                _logger.LogWarning(ex, "Embedding failed for {InteractionId}; continuing without semantic signal", interaction.Id);
            }
        }

        await _repository.UpsertInteractionAsync(interaction, ct);

        var (key, _) = interaction.Customer.ResolveKey();
        if (key is null)
        {
            var (policy, rates) = await _configuration.GetAsync(ct);
            var single = new ResolutionEngine(policy, rates).Evaluate([interaction], _time.GetUtcNow());
            await _repository.SaveEvaluationAsync(null, [interaction], single, interaction.StartedOn, ct);
            return new ProcessingOutcome(interaction.Id, null, 1, 1, false);
        }

        return await EvaluateCustomerAsync(key, ct) with { InteractionId = interaction.Id };
    }

    /// <summary>Re-evaluates one customer. Used for new contacts and for maturing verdicts whose window has closed.</summary>
    public async Task<ProcessingOutcome> EvaluateCustomerAsync(string customerKey, CancellationToken ct = default)
    {
        var (policy, rates) = await _configuration.GetAsync(ct);
        var asOf = _time.GetUtcNow();

        // Two windows back: an interaction inside one window may link to a predecessor up to one window earlier.
        var since = asOf - policy.MaxWindow - policy.MaxWindow;
        var history = await _repository.LoadCustomerHistoryAsync(customerKey, since, MaxHistoryPerCustomer, ct);

        var tooBroad = history.Count > MaxHistoryPerCustomer;
        if (tooBroad)
        {
            _logger.LogWarning(
                "Customer key {CustomerKey} has more than {Max} interactions in horizon; treating identity as unreliable",
                customerKey, MaxHistoryPerCustomer);
            history = history
                .Select(i => i with { Customer = CustomerIdentity.FromKnownKey(customerKey, IdentityConfidence.Low, i.Customer.ContactId, i.Customer.AccountId) })
                .ToList();
        }

        var engine = new ResolutionEngine(policy, rates, new SameIssueScorer(policy));
        var result = engine.Evaluate(history, asOf);
        await _repository.SaveEvaluationAsync(customerKey, history, result, since, ct);

        _logger.LogInformation(
            "Evaluated {Count} interactions / {Episodes} episodes for {CustomerKey}",
            history.Count, result.Episodes.Count, customerKey);

        return new ProcessingOutcome(Guid.Empty, customerKey, history.Count, result.Interactions.Count, tooBroad);
    }

    /// <summary>Timer-driven: finalises verdicts whose repeat windows have closed with no return contact.</summary>
    public async Task<int> MatureDueVerdictsAsync(int maxCustomers = 500, CancellationToken ct = default)
    {
        var keys = await _repository.FindCustomerKeysDueForMaturationAsync(_time.GetUtcNow(), maxCustomers, ct);
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            await EvaluateCustomerAsync(key, ct);
        }
        return keys.Count;
    }
}
