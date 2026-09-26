using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using Vrl.AI;
using Vrl.Core.Abstractions;
using Vrl.Core.Processing;
using Vrl.Core.Similarity;
using Vrl.Dataverse;
using Vrl.Dataverse.Adapters;
using Vrl.Functions;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.Configure<LedgerOptions>(builder.Configuration.GetSection(LedgerOptions.Section));

// One credential for everything: user-assigned managed identity in Azure (AZURE_CLIENT_ID), developer sign-in locally.
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

// Dataverse: a single ServiceClient is thread-safe for request execution and pools connections.
builder.Services.AddSingleton<IOrganizationServiceAsync2>(sp =>
{
    var o = sp.GetRequiredService<IOptions<LedgerOptions>>().Value;
    if (string.IsNullOrWhiteSpace(o.DataverseUrl)) throw new InvalidOperationException("Vrl__DataverseUrl is not configured.");

    TokenCredential credential = !string.IsNullOrWhiteSpace(o.DataverseClientSecret)
        ? new ClientSecretCredential(o.DataverseTenantId, o.DataverseClientId, o.DataverseClientSecret)
        : sp.GetRequiredService<TokenCredential>();

    return DataverseClientFactory.Create(new Uri(o.DataverseUrl), credential, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Dataverse"));
});

builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
{
    var o = sp.GetRequiredService<IOptions<LedgerOptions>>().Value;
    return !string.IsNullOrWhiteSpace(o.AoaiEndpoint) && !string.IsNullOrWhiteSpace(o.AoaiDeployment)
        ? new AzureOpenAIEmbeddingProvider(new Uri(o.AoaiEndpoint), o.AoaiDeployment, o.AoaiDimensions,
            sp.GetRequiredService<TokenCredential>(), o.AoaiModel)
        : new LocalHashingEmbeddingProvider();
});

builder.Services.AddSingleton<ILedgerRepository>(sp => new DataverseLedgerRepository(
    sp.GetRequiredService<IOrganizationServiceAsync2>(),
    sp.GetRequiredService<ILogger<DataverseLedgerRepository>>(),
    sp.GetRequiredService<IEmbeddingProvider>().ModelId));

builder.Services.AddSingleton<ILedgerConfigurationProvider>(sp => new DataverseLedgerConfigurationProvider(
    sp.GetRequiredService<IOrganizationServiceAsync2>(),
    sp.GetRequiredService<ILogger<DataverseLedgerConfigurationProvider>>(),
    sp.GetRequiredService<IEmbeddingProvider>().ModelId));

builder.Services.AddSingleton<LedgerProcessor>(sp => new LedgerProcessor(
    sp.GetRequiredService<ILedgerRepository>(),
    sp.GetRequiredService<ILedgerConfigurationProvider>(),
    sp.GetRequiredService<IEmbeddingProvider>(),
    TimeProvider.System,
    sp.GetRequiredService<ILogger<LedgerProcessor>>()));

// Adapter options from app settings, e.g. Vrl__Adapter__IdentifyFromSelfDeclaredEmail=false.
builder.Services.AddSingleton(builder.Configuration.GetSection("Vrl:Adapter").Get<OmnichannelAdapterOptions>() ?? new OmnichannelAdapterOptions());
builder.Services.AddSingleton<OmnichannelConversationAdapter>();
builder.Services.AddSingleton<BotTopicEnricher>();

// Service Bus sender for the session queue (identity-based, same namespace as the trigger connection).
builder.Services.AddSingleton(sp =>
{
    var ns = builder.Configuration["ServiceBusConnection__fullyQualifiedNamespace"]
             ?? builder.Configuration["ServiceBusConnection:fullyQualifiedNamespace"]
             ?? throw new InvalidOperationException("ServiceBusConnection__fullyQualifiedNamespace is not configured.");
    return new ServiceBusClient(ns, sp.GetRequiredService<TokenCredential>());
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<LedgerOptions>>().Value;
    return sp.GetRequiredService<ServiceBusClient>().CreateSender(o.InteractionsQueue);
});
builder.Services.AddSingleton<CommandPublisher>();

builder.Build().Run();
