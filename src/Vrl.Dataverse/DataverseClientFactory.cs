using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Vrl.Dataverse;

/// <summary>
/// Creates a Dataverse <see cref="ServiceClient"/> authenticated with an Entra ID token credential.
/// In Azure this is the Function App's user-assigned managed identity (registered as a Dataverse application user),
/// locally it is your Azure CLI / Visual Studio sign-in – no client secrets anywhere.
/// </summary>
public static class DataverseClientFactory
{
    public static ServiceClient Create(Uri environmentUrl, TokenCredential? credential = null, ILogger? logger = null)
    {
        credential ??= new DefaultAzureCredential();
        var scope = $"{environmentUrl.GetLeftPart(UriPartial.Authority)}/.default";

        var client = new ServiceClient(
            environmentUrl,
            async _ => (await credential.GetTokenAsync(new TokenRequestContext([scope]), CancellationToken.None)).Token,
            useUniqueInstance: true,
            logger: logger);

        if (!client.IsReady)
            throw new InvalidOperationException($"Could not connect to Dataverse at {environmentUrl}: {client.LastError}", client.LastException);

        // Let the SDK honour Retry-After on service-protection (429) responses.
        client.MaxRetryCount = 5;
        client.RetryPauseTime = TimeSpan.FromSeconds(5);
        return client;
    }

    /// <summary>Builds the credential for a user-assigned managed identity when a client id is supplied.</summary>
    public static TokenCredential CredentialFor(string? managedIdentityClientId) =>
        string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId });
}
