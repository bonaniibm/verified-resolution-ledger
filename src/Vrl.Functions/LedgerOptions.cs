using System.Text.Json;
using System.Text.Json.Serialization;
using Vrl.Core.Model;

namespace Vrl.Functions;

public sealed class LedgerOptions
{
    public const string Section = "Vrl";

    public string DataverseUrl { get; set; } = "";

    /// <summary>
    /// Cross-tenant fallback. Leave empty when the Function App's managed identity lives in the same Entra tenant as
    /// Dataverse (recommended). When Dataverse is in a different tenant (e.g. a Microsoft developer tenant), register an
    /// app there and supply its credentials – the secret should be a Key Vault reference, never a plain app setting.
    /// </summary>
    public string? DataverseTenantId { get; set; }
    public string? DataverseClientId { get; set; }
    public string? DataverseClientSecret { get; set; }

    public string? AoaiEndpoint { get; set; }
    public string? AoaiDeployment { get; set; }
    public string? AoaiModel { get; set; }
    public int AoaiDimensions { get; set; } = 512;

    public string InteractionsQueue { get; set; } = "vrl-interactions";
    public int MaturationBatch { get; set; } = 500;
}

/// <summary>Message contract on the session-enabled interactions queue (SessionId = customer key).</summary>
public sealed record LedgerCommand
{
    public const string Ingest = "ingest";
    public const string Evaluate = "evaluate";

    public required string Type { get; init; }
    public Interaction? Interaction { get; init; }
    public string? CustomerKey { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
