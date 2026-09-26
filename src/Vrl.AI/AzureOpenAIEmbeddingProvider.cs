using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using OpenAI.Embeddings;
using Vrl.Core.Similarity;

namespace Vrl.AI;

/// <summary>
/// Azure OpenAI / AI Foundry embeddings via Entra ID (managed identity) – no API keys.
/// Recommended deployment: text-embedding-3-small with 512 dimensions, which keeps each stored vector ~2.7 KB of
/// base64 in Dataverse while retaining good paraphrase recall for short support summaries.
/// </summary>
public sealed class AzureOpenAIEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly EmbeddingGenerationOptions _options;

    public AzureOpenAIEmbeddingProvider(
        Uri endpoint, string deployment, int? dimensions = 512, TokenCredential? credential = null, string? modelName = null)
    {
        var client = new AzureOpenAIClient(endpoint, credential ?? new DefaultAzureCredential());
        _client = client.GetEmbeddingClient(deployment);
        _options = new EmbeddingGenerationOptions { Dimensions = dimensions };
        // Model name (not deployment name) drives similarity calibration – see SimilarityWeights.ForEmbeddingModel.
        ModelId = $"aoai:{modelName ?? deployment}:{dimensions?.ToString() ?? "native"}";
    }

    public string ModelId { get; }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        // Summaries are short; cap input defensively to stay well inside token limits.
        var input = text.Length > 8000 ? text[..8000] : text;
        var result = await _client.GenerateEmbeddingAsync(input, _options, cancellationToken);
        return VectorMath.Normalize(result.Value.ToFloats().ToArray());
    }
}
