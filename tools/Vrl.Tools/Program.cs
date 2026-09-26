using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vrl.AI;
using Vrl.Core.Abstractions;
using Vrl.Core.Analytics;
using Vrl.Core.Costing;
using Vrl.Core.Model;
using Vrl.Core.Policy;
using Vrl.Core.Processing;
using Vrl.Core.Similarity;
using Vrl.Core.Synthetic;
using Vrl.Dataverse;
using Vrl.Dataverse.Adapters;
using Vrl.Dataverse.Schema;
using S = Vrl.Dataverse.Schema.LedgerSchema;

// Verified Resolution Ledger – operator CLI.
//   vrl simulate            [--customers 600] [--days 30] [--seed 42] [--out ./out]
//   vrl provision           --url https://org.crm.dynamics.com
//   vrl seed-config         --url ...
//   vrl seed                --url ... [--customers 150] [--days 30] [--seed 42] [--parallel 4] [--aoai-endpoint ... --aoai-deployment ...]
//   vrl mature              --url ...
//   vrl inspect-conversation --url ... --id <msdyn_ocliveworkitem id>
//   vrl deploy-webresources  --url ... [--path webresources/dist]
// Authentication: Azure CLI sign-in. --tenant <Dataverse tenant> and --azure-tenant <Azure tenant> select the credential
// for each side when they differ. No secrets are read from the command line.

var cmd = args.FirstOrDefault()?.ToLowerInvariant();
var opts = ParseOptions(args.Skip(1).ToArray());

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(opts.ContainsKey("verbose") ? LogLevel.Debug : LogLevel.Information)
    .AddFilter("Microsoft.PowerPlatform", LogLevel.Warning)
    // The Dataverse SDK logs every fault with full stack traces; show them only with --verbose.
    .AddFilter("dataverse", opts.ContainsKey("verbose") ? LogLevel.Debug : LogLevel.None));
var log = loggerFactory.CreateLogger("vrl");

try
{
    switch (cmd)
    {
        case "simulate": await Simulate(); break;
        case "provision": await Provision(); break;
        case "seed-config": SeedConfig(); break;
        case "seed": await Seed(); break;
        case "mature": await Mature(); break;
        case "inspect-conversation": await Inspect(); break;
        case "deploy-webresources": DeployWebResources(); break;
        case "verify": Verify(); break;
        case "assign-app-user": AssignAppUser(); break;
        case "show": Show(); break;
        case "register-endpoint": RegisterEndpoint(); break;
        case "promote": await Promote(); break;
        case "diag": Diag(); break;
        case "ingest-conversation": await IngestConversation(); break;
        case "explain": await Explain(); break;
        case "reingest": await Reingest(); break;
        case "solution-report": SolutionReport(); break;
        case "package": await Package(); break;
        case "import-solution": await ImportSolution(); break;
        case "bot-transcripts": BotTranscripts(); break;
        case "dump-transcript": await DumpTranscript(); break;
        default:
            Console.WriteLine("Commands: simulate | provision | seed-config | seed | mature | inspect-conversation   (see README)");
            return 1;
    }
    return 0;
}
catch (Exception ex)
{
    if (opts.ContainsKey("verbose")) log.LogError(ex, "Command failed");
    else log.LogError("Command failed: {Message}  (re-run with --verbose for details)", ex.Message);
    return 2;
}

// ---------------------------------------------------------------------------------------------------------------------

async Task Simulate()
{
    var end = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
    var data = new SyntheticContactCenter(new SyntheticOptions
    {
        Customers = Int("customers", 600), Days = Int("days", 30), Seed = Int("seed", 42), End = end,
    }).Generate();

    var embeddings = new LocalHashingEmbeddingProvider();
    var policy = new LedgerPolicy { Weights = SimilarityWeights.ForEmbeddingModel(embeddings.ModelId) };
    var repo = new InMemoryLedgerRepository();
    var clock = new ManualClock(end.AddDays(-Int("days", 30)));
    var processor = new LedgerProcessor(repo, new StaticLedgerConfigurationProvider(policy, CostRateCard.Default), embeddings, clock);

    // Replay events in the order Dataverse would emit them: on conversation close.
    foreach (var i in data.Interactions.OrderBy(i => i.EndedOn))
    {
        clock.Now = i.EndedOn;
        await processor.IngestAsync(i with { Id = Guid.Empty });
    }
    clock.Now = end;
    await processor.MatureDueVerdictsAsync(maxCustomers: 100_000);

    // Rebuild a consolidated evaluation for reporting.
    var stored = repo.Interactions.ToList();
    var result = new Vrl.Core.Engine.EvaluationResult(repo.Evaluations.ToList(), repo.Episodes.ToList(), Vrl.Core.Engine.ResolutionEngine.Version);
    var kpis = LedgerKpiCalculator.Calculate(stored, result);

    // Ground truth keyed by deterministic ID.
    var truth = data.Interactions.ToDictionary(
        i => Vrl.Core.Util.DeterministicGuid.ForInteraction(i.SourceSystem, i.SourceRecordId),
        i => data.Truth[i.Id] with { InteractionId = Vrl.Core.Util.DeterministicGuid.ForInteraction(i.SourceSystem, i.SourceRecordId) });
    var accuracy = AccuracyReport.Compute(new SyntheticDataset(stored, truth), result, policy.DefaultWindow);

    PrintKpis(kpis, accuracy);

    var outDir = Str("out", "out")!;
    Directory.CreateDirectory(outDir);
    await File.WriteAllTextAsync(Path.Combine(outDir, "kpis.json"), JsonSerializer.Serialize(new { kpis, accuracy }, JsonOut));
    await WriteCsv(Path.Combine(outDir, "interactions.csv"), stored, result);
    await WriteDemoJson(Path.Combine(outDir, "dashboard-demo.json"), stored, result, end);
    log.LogInformation("Wrote {Dir}/kpis.json, interactions.csv and dashboard-demo.json", outDir);
}

async Task Provision()
{
    using var client = Connect();
    if (LedgerSolutionIsManaged(client))
    {
        // Changing managed components would create unmanaged layers that block future upgrades.
        log.LogInformation("Managed solution {Solution} is installed: schema and roles come from the package; provisioning skipped", S.SolutionUniqueName);
        return;
    }
    // Freshly created environments (e.g. a Contact Center trial still installing its first-party solutions) can
    // return transient server-side SQL timeouts on metadata operations. Provisioning is idempotent, so back off and
    // resume: completed components are detected and skipped on the next pass.
    var delays = new[] { 60, 120, 240, 300 };
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await new SchemaProvisioner(client, log).ProvisionAsync();
            return;
        }
        catch (Exception ex) when (attempt <= delays.Length && IsTransient(ex))
        {
            var wait = delays[attempt - 1];
            log.LogWarning("Transient Dataverse error on attempt {Attempt} ({Reason}); resuming in {Seconds}s",
                attempt, FirstLine(ex.Message), wait);
            await Task.Delay(TimeSpan.FromSeconds(wait));
        }
    }

    static bool IsTransient(Exception ex)
    {
        var m = ex.ToString();
        return m.Contains("timeout period elapsed", StringComparison.OrdinalIgnoreCase)
            || m.Contains("SqlException", StringComparison.OrdinalIgnoreCase)
            || m.Contains("server is not responding", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Generic SQL error", StringComparison.OrdinalIgnoreCase)
            || m.Contains("0x80044150", StringComparison.OrdinalIgnoreCase)       // generic SQL error
            || m.Contains("Combined execution time", StringComparison.OrdinalIgnoreCase) // service protection
            || m.Contains("try again later", StringComparison.OrdinalIgnoreCase)
            || m.Contains("another customization operation", StringComparison.OrdinalIgnoreCase); // concurrent solution install
    }

    static string FirstLine(string s) { var i = s.IndexOf('.'); return i > 0 && i < 160 ? s[..i] : (s.Length > 160 ? s[..160] : s); }
}

void SeedConfig()
{
    using var client = Connect();
    var rows = new List<Entity>
    {
        Policy("Global default – 72h", S.PolicyScope.Global, 72, threshold: 0.60m),
        Policy("Case – 7 days", S.PolicyScope.Channel, 168, channel: Channel.Case),
        Rate("Copilot credit price", S.RateType.CreditPrice, 0.01m),
        Rate("Telephony per minute (ACS)", S.RateType.TelephonyPerMinute, 0.02m),
        Rate("Loaded labor – default", S.RateType.LaborPerHour, 35m),
    };

    foreach (var row in rows)
    {
        var exists = client.RetrieveMultiple(new QueryExpression(row.LogicalName)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = { Conditions = { new ConditionExpression("bpc_name", ConditionOperator.Equal, row["bpc_name"]) } },
        }).Entities.Count > 0;
        if (exists) { log.LogInformation("Exists: {Name}", row["bpc_name"]); continue; }
        client.Create(row);
        log.LogInformation("Created: {Name}", row["bpc_name"]);
    }

    static Entity Policy(string name, S.PolicyScope scope, int hours, decimal? threshold = null, Channel? channel = null)
    {
        var e = new Entity(S.WindowPolicy.Table)
        {
            [S.WindowPolicy.Name] = name,
            [S.WindowPolicy.Scope] = new OptionSetValue((int)scope),
            [S.WindowPolicy.WindowHours] = hours,
        };
        if (threshold is { } t) e[S.WindowPolicy.SameIssueThreshold] = t;
        if (channel is { } c) e[S.WindowPolicy.Channel] = new OptionSetValue((int)c);
        return e;
    }

    static Entity Rate(string name, S.RateType type, decimal amount) => new(S.CostRate.Table)
    {
        [S.CostRate.Name] = name,
        [S.CostRate.RateType] = new OptionSetValue((int)type),
        [S.CostRate.Amount] = amount,
        [S.CostRate.Currency] = "EUR",
    };
}

async Task Seed()
{
    using var root = Connect();
    var embeddings = Embeddings();
    var end = DateTimeOffset.UtcNow;
    var data = new SyntheticContactCenter(new SyntheticOptions
    {
        Customers = Int("customers", 150), Days = Int("days", 30), Seed = Int("seed", 42), End = end,
    }).Generate();

    // Synthetic contacts must exist for the contact lookups (and the agent side pane) to resolve.
    var contactIds = data.Interactions.Select(i => i.Customer.ContactId).OfType<Guid>().Distinct().ToList();
    log.LogInformation("Ensuring {Count} synthetic contacts...", contactIds.Count);
    var n = 0;
    foreach (var id in contactIds)
    {
        n++;
        root.Execute(new Microsoft.Xrm.Sdk.Messages.UpsertRequest
        {
            Target = new Entity("contact", id)
            {
                ["firstname"] = "Synthetic",
                ["lastname"] = $"Customer {n:D4}",
                ["description"] = "Generated by Verified Resolution Ledger seed. Safe to delete.",
            },
        });
    }

    var byCustomer = data.Interactions.GroupBy(i => i.Customer.ResolveKey().Key ?? i.Id.ToString()).ToList();
    log.LogInformation("Ingesting {Interactions} interactions for {Customers} customer keys...", data.Interactions.Count, byCustomer.Count);

    var done = 0;
    await Parallel.ForEachAsync(byCustomer, new ParallelOptions { MaxDegreeOfParallelism = Int("parallel", 4) }, async (group, ct) =>
    {
        using var client = root.Clone();
        var repo = new DataverseLedgerRepository(client, loggerFactory.CreateLogger<DataverseLedgerRepository>(), embeddings.ModelId);
        var config = new DataverseLedgerConfigurationProvider(client, loggerFactory.CreateLogger<DataverseLedgerConfigurationProvider>(), embeddings.ModelId);
        var processor = new LedgerProcessor(repo, config, embeddings, logger: loggerFactory.CreateLogger<LedgerProcessor>());
        foreach (var i in group.OrderBy(i => i.EndedOn))
            await processor.IngestAsync(i with { Id = Guid.Empty }, ct);
        var c = Interlocked.Increment(ref done);
        if (c % 25 == 0) log.LogInformation("  {Done}/{Total} customers", c, byCustomer.Count);
    });
    log.LogInformation("Seed complete.");
}

async Task Mature()
{
    using var client = Connect();
    var embeddings = Embeddings();
    var processor = new LedgerProcessor(
        new DataverseLedgerRepository(client, loggerFactory.CreateLogger<DataverseLedgerRepository>(), embeddings.ModelId),
        new DataverseLedgerConfigurationProvider(client, loggerFactory.CreateLogger<DataverseLedgerConfigurationProvider>(), embeddings.ModelId),
        embeddings);
    var count = await processor.MatureDueVerdictsAsync(Int("max", 1000));
    log.LogInformation("Re-evaluated {Count} customer(s) with matured windows", count);
}

async Task Inspect()
{
    using var client = Connect();
    var adapter = new OmnichannelConversationAdapter(client, loggerFactory.CreateLogger<OmnichannelConversationAdapter>(), AdapterOptions());
    var interaction = await adapter.BuildAsync(Guid.Parse(Str("id") ?? throw new ArgumentException("--id is required")));
    if (interaction is null) { log.LogInformation("Conversation is not closed; nothing to map."); return; }
    Console.WriteLine(JsonSerializer.Serialize(ForDisplay(interaction), JsonOut));
}

async Task IngestConversation()
{
    // Maps one Omnichannel conversation through the adapter and ingests it with the operator's identity.
    // Used to replay a conversation whose Service Bus message was dead-lettered.
    using var client = Connect();
    var id = Guid.Parse(Str("id") ?? throw new ArgumentException("--id is required"));
    var embeddings = Embeddings();
    var adapter = new OmnichannelConversationAdapter(client, loggerFactory.CreateLogger<OmnichannelConversationAdapter>(), AdapterOptions());
    var interaction = await adapter.BuildAsync(id) ?? throw new InvalidOperationException("Conversation is not closed.");
    Console.WriteLine(JsonSerializer.Serialize(ForDisplay(interaction), JsonOut));
    var processor = new LedgerProcessor(
        new DataverseLedgerRepository(client, loggerFactory.CreateLogger<DataverseLedgerRepository>(), embeddings.ModelId),
        new DataverseLedgerConfigurationProvider(client, loggerFactory.CreateLogger<DataverseLedgerConfigurationProvider>(), embeddings.ModelId),
        embeddings, logger: loggerFactory.CreateLogger<LedgerProcessor>());
    var outcome = await processor.IngestAsync(interaction);
    log.LogInformation("Ingested {Id}: customer {Key}, history {History}", outcome.InteractionId, outcome.CustomerKey, outcome.HistorySize);
}

void SolutionReport()
{
    // Read-only: every component of the ledger solution with its type and name, so the package scope can be reviewed.
    using var client = Connect();
    var name = Str("solution") ?? S.SolutionUniqueName;
    foreach (var c in SolutionInspector.Components(client, name))
        Console.WriteLine($"  {c.Type,6}  {c.TypeName,-26} {c.Name}{(c.Behavior == 0 ? "" : $"  (behavior {c.Behavior})")}");
}

async Task DumpTranscript()
{
    // Read-only diagnostic: saves one conversation's Omnichannel transcript JSON (for validating the message format).
    using var client = Connect();
    var id = Guid.Parse(Str("id") ?? throw new ArgumentException("--id is required"));
    var output = Str("out") ?? $"omnichannel-transcript-{id}.json";
    var adapter = new OmnichannelConversationAdapter(client, loggerFactory.CreateLogger<OmnichannelConversationAdapter>(), AdapterOptions());
    var json = await adapter.RawTranscriptAsync(id) ?? throw new InvalidOperationException("No transcript for this conversation.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, json);
    log.LogInformation("Saved {Chars:N0} chars to {File}", json.Length, output);
}

void BotTranscripts()
{
    // Read-only diagnostic: lists recent Copilot Studio transcripts (conversationtranscript) and optionally saves their
    // content, to validate how they correlate with Omnichannel conversations and how topics are traced.
    using var client = Connect();
    var since = DateTime.UtcNow.AddHours(-Int("since-hours", 24));
    var dump = Str("dump");
    if (dump is not null) Directory.CreateDirectory(dump);
    var q = new QueryExpression("conversationtranscript")
    {
        ColumnSet = new ColumnSet(true),
        TopCount = Int("top", 20),
        Orders = { new OrderExpression("createdon", OrderType.Descending) },
        Criteria = { Conditions = { new ConditionExpression("createdon", ConditionOperator.GreaterEqual, since) } },
    };
    var rows = client.RetrieveMultiple(q).Entities;
    Console.WriteLine($"  {rows.Count} transcript(s) since {since:u}");
    var guid = new System.Text.RegularExpressions.Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
    foreach (var r in rows)
    {
        var content = r.GetAttributeValue<string>("content") ?? "";
        var bot = r.GetAttributeValue<EntityReference>("bot_conversationtranscriptid");
        Console.WriteLine($"\n  {r.Id}  created {r.GetAttributeValue<DateTime>("createdon"):u}  start {r.GetAttributeValue<DateTime?>("conversationstarttime"):u}  bot {bot?.Name}  name {r.GetAttributeValue<string>("name")}  {content.Length:N0} chars");
        Console.WriteLine($"    attributes: {string.Join(", ", r.Attributes.Keys.Where(k => k != "content").OrderBy(k => k))}");
        // Which Omnichannel conversations (msdyn_ocliveworkitem) does this transcript mention?
        var ids = guid.Matches(content).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        var known = ids.Where(id => { try { client.Retrieve("msdyn_ocliveworkitem", Guid.Parse(id), new ColumnSet(false)); return true; } catch { return false; } }).ToList();
        Console.WriteLine($"    GUIDs in content: {ids.Count}; matching Omnichannel conversations: {string.Join(", ", known)}");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("activities", out var acts))
            {
                var kinds = acts.EnumerateArray().Select(a =>
                    (a.TryGetProperty("type", out var t) ? t.GetString() : "?") + (a.TryGetProperty("valueType", out var vt) ? "/" + vt.GetString() : ""))
                    .GroupBy(k => k).Select(g => $"{g.Key} x{g.Count()}");
                Console.WriteLine($"    activities: {string.Join("; ", kinds)}");
            }
        }
        catch (System.Text.Json.JsonException) { Console.WriteLine("    content is not JSON"); }
        if (dump is not null) File.WriteAllText(Path.Combine(dump, $"transcript-{r.Id}.json"), content);
    }
}

async Task ImportSolution()
{
    // Installs or upgrades the managed ledger solution:
    //   not installed                  -> import
    //   managed, older version         -> import as holding solution, then DeleteAndPromote (true upgrade: removed
    //                                     components are deleted, data is kept)
    //   managed, same or newer version -> nothing to do
    //   unmanaged (a dev environment)  -> refuse: a managed layer on top of unmanaged components breaks ALM
    var file = Str("file") ?? throw new ArgumentException("--file <managed solution zip> is required");
    if (!File.Exists(file)) throw new FileNotFoundException($"Solution file not found: {file}");
    var bytes = await File.ReadAllBytesAsync(file);
    var (uniqueName, version, managed) = ReadSolutionManifest(bytes);
    if (uniqueName != S.SolutionUniqueName) throw new InvalidOperationException($"{file} contains solution '{uniqueName}', expected '{S.SolutionUniqueName}'.");
    if (!managed) throw new InvalidOperationException($"{file} is an unmanaged solution. Install the *_managed.zip; unmanaged is for development only.");

    Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.MaxConnectionTimeout = TimeSpan.FromMinutes(10);
    using var client = Connect();
    var installed = client.RetrieveMultiple(new QueryExpression("solution")
    {
        ColumnSet = new ColumnSet("version", "ismanaged"),
        Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, S.SolutionUniqueName) } },
    }).Entities.FirstOrDefault();

    var upgrade = false;
    if (installed is not null)
    {
        var current = Version.Parse(installed.GetAttributeValue<string>("version"));
        if (!installed.GetAttributeValue<bool>("ismanaged"))
            throw new InvalidOperationException(
                $"This environment has the UNMANAGED ledger solution {current} (a development environment). " +
                "Do not install the managed package here; use the source path (Deploy.cmd with \"install\": \"source\").");
        if (current >= version) { log.LogInformation("Managed {Solution} {Current} already installed (package {Version}); nothing to do", S.SolutionUniqueName, current, version); return; }
        upgrade = true;
        log.LogInformation("Upgrading managed {Solution} {Current} -> {Version}", S.SolutionUniqueName, current, version);
    }
    else log.LogInformation("Installing managed {Solution} {Version}", S.SolutionUniqueName, version);

    var resp = WithSolutionLockRetry(() => (Microsoft.Crm.Sdk.Messages.ImportSolutionAsyncResponse)client.Execute(new Microsoft.Crm.Sdk.Messages.ImportSolutionAsyncRequest
    {
        CustomizationFile = bytes,
        HoldingSolution = upgrade,
        OverwriteUnmanagedCustomizations = false,   // never silently discard someone's customisations
        PublishWorkflows = true,
    }), "import");
    await WaitForAsyncOperation(client, resp.AsyncOperationId, "import");

    if (upgrade)
    {
        log.LogInformation("Applying upgrade (DeleteAndPromote)...");
        WithSolutionLockRetry(() => client.Execute(new Microsoft.Crm.Sdk.Messages.DeleteAndPromoteRequest { UniqueName = S.SolutionUniqueName }), "upgrade");
    }
    WithSolutionLockRetry(() => client.Execute(new Microsoft.Crm.Sdk.Messages.PublishAllXmlRequest()), "publish");
    log.LogInformation("Managed {Solution} {Version} is installed", S.SolutionUniqueName, version);
}

async Task WaitForAsyncOperation(Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client, Guid asyncId, string what)
{
    for (var i = 0; i < 180; i++)   // up to 30 minutes
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        var op = client.Retrieve("asyncoperation", asyncId, new ColumnSet("statecode", "statuscode", "message", "friendlymessage"));
        var state = op.GetAttributeValue<OptionSetValue>("statecode")?.Value;
        var status = op.GetAttributeValue<OptionSetValue>("statuscode")?.Value;
        if (i % 3 == 0) log.LogInformation("  {What} status {Status}", what, status);
        if (state != 3) continue; // 3 = Completed
        if (status == 30) return;  // Succeeded
        var msg = op.GetAttributeValue<string>("friendlymessage") ?? op.GetAttributeValue<string>("message");
        throw new InvalidOperationException($"Solution {what} ended with status {status}: {msg}");
    }
    throw new TimeoutException($"Solution {what} did not finish within 30 minutes (async operation {asyncId}).");
}

static (string UniqueName, Version Version, bool Managed) ReadSolutionManifest(byte[] zipBytes)
{
    using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes));
    var entry = zip.GetEntry("solution.xml") ?? throw new InvalidOperationException("Not a Dataverse solution file (solution.xml missing).");
    using var r = new StreamReader(entry.Open());
    var manifest = System.Xml.Linq.XDocument.Parse(r.ReadToEnd()).Descendants("SolutionManifest").Single();
    return ((string)manifest.Element("UniqueName")!, Version.Parse((string)manifest.Element("Version")!), (string?)manifest.Element("Managed") == "1");
}

static bool LedgerSolutionIsManaged(Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client) =>
    client.RetrieveMultiple(new QueryExpression("solution")
    {
        ColumnSet = new ColumnSet("ismanaged"),
        Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, S.SolutionUniqueName) } },
    }).Entities.FirstOrDefault()?.GetAttributeValue<bool>("ismanaged") == true;

async Task Package()
{
    // Builds the distributable Dataverse solution from a source environment:
    //  1. curates the solution to an allow-list (removing a component from the solution never deletes it),
    //  2. stamps the version and publishes,
    //  3. exports unmanaged + managed zips,
    //  4. opens the managed zip and fails if anything outside the allow-list is inside.
    using var client = Connect();
    var version = Str("version") ?? throw new ArgumentException("--version is required, e.g. 1.0.0.0");
    if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+\.\d+$")) throw new ArgumentException("--version must be major.minor.build.revision");
    var outDir = Str("out") ?? Path.Combine("artifacts", "solution");
    var apply = opts.ContainsKey("curate");
    Directory.CreateDirectory(outDir);

    var components = SolutionInspector.Components(client, S.SolutionUniqueName);
    var remove = components.Where(c => !PackagePolicy.IsAllowed(c)).ToList();
    foreach (var c in remove)
        Console.WriteLine($"  {(apply ? "remove" : "WOULD remove")}  {c.TypeName,-18} {c.Name}");
    if (remove.Count > 0 && !apply)
        throw new InvalidOperationException($"{remove.Count} component(s) are outside the package allow-list. Re-run with --curate to remove them from the solution (they stay in the environment).");
    foreach (var c in remove.OrderByDescending(c => c.Type))   // children (bot topics) before parents
    {
        try
        {
            client.Execute(new Microsoft.Crm.Sdk.Messages.RemoveSolutionComponentRequest
            {
                ComponentId = c.Id, ComponentType = c.Type, SolutionUniqueName = S.SolutionUniqueName,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("  could not remove {Type} {Name}: {Message}", c.TypeName, c.Name, ex.Message);
        }
    }

    var sol = client.RetrieveMultiple(new QueryExpression("solution")
    {
        ColumnSet = new ColumnSet("solutionid"),
        Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, S.SolutionUniqueName) } },
    }).Entities.Single();
    client.Update(new Entity("solution", sol.Id) { ["version"] = version });
    log.LogInformation("Publishing customizations...");
    WithSolutionLockRetry(() => client.Execute(new Microsoft.Crm.Sdk.Messages.PublishAllXmlRequest()), "publish");

    var files = new List<string>();
    foreach (var managed in new[] { false, true })
    {
        var export = WithSolutionLockRetry(() => (Microsoft.Crm.Sdk.Messages.ExportSolutionResponse)client.Execute(new Microsoft.Crm.Sdk.Messages.ExportSolutionRequest
        {
            SolutionName = S.SolutionUniqueName, Managed = managed,
        }), "export");
        var file = Path.Combine(outDir, $"{S.SolutionUniqueName}_{version.Replace('.', '_')}{(managed ? "_managed" : "")}.zip");
        await File.WriteAllBytesAsync(file, export.ExportSolutionFile);
        files.Add(file);
        log.LogInformation("Exported {File} ({Bytes:N0} bytes)", file, export.ExportSolutionFile.Length);
    }

    var problems = PackagePolicy.Verify(files[1]);
    foreach (var p in problems) log.LogError("  package check: {Problem}", p);
    if (problems.Count > 0) throw new InvalidOperationException("The managed package contains components outside the allow-list.");
    log.LogInformation("Package verified: only ledger components inside {File}", files[1]);
}

async Task Reingest()
{
    // Replays every ledger conversation of one source through the CURRENT adapter and engine (idempotent upserts).
    // Use after an adapter or scoring upgrade so historic rows are mapped and explained the same way as new ones.
    using var client = Connect();
    var source = Str("source") ?? OmnichannelConversationAdapter.SourceSystem;
    if (source != OmnichannelConversationAdapter.SourceSystem) throw new ArgumentException("Only --source omnichannel can be re-mapped from its source records.");
    var ids = client.RetrieveMultiple(new QueryExpression(S.Interaction.Table)
    {
        ColumnSet = new ColumnSet(S.Interaction.SourceRecordId),
        Criteria = { Conditions = { new ConditionExpression(S.Interaction.SourceSystem, ConditionOperator.Equal, source) } },
        Orders = { new OrderExpression(S.Interaction.StartedOn, OrderType.Ascending) },
    }).Entities.Select(e => e.GetAttributeValue<string>(S.Interaction.SourceRecordId)).Where(x => Guid.TryParse(x, out _)).ToList();

    var embeddings = Embeddings();
    var adapter = new OmnichannelConversationAdapter(client, loggerFactory.CreateLogger<OmnichannelConversationAdapter>(), AdapterOptions());
    var processor = new LedgerProcessor(
        new DataverseLedgerRepository(client, loggerFactory.CreateLogger<DataverseLedgerRepository>(), embeddings.ModelId),
        new DataverseLedgerConfigurationProvider(client, loggerFactory.CreateLogger<DataverseLedgerConfigurationProvider>(), embeddings.ModelId),
        embeddings, logger: loggerFactory.CreateLogger<LedgerProcessor>());
    int ok = 0, skipped = 0, failed = 0;
    foreach (var id in ids)
    {
        try
        {
            var interaction = await adapter.BuildAsync(Guid.Parse(id));
            if (interaction is null) { skipped++; continue; }
            await processor.IngestAsync(interaction);
            ok++;
            log.LogInformation("  {Id}: {Mode}, {Native}", id[..8], interaction.HandlingMode, interaction.NativeBotOutcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed++;
            log.LogWarning("  {Id}: {Message}", id[..8], ex.Message);
        }
    }
    log.LogInformation("Re-ingested {Ok} conversation(s); {Skipped} skipped (not closed); {Failed} failed", ok, skipped, failed);
}

async Task Explain()
{
    // Read-only: loads one customer's ledger history and prints the same-issue score of every later/earlier pair,
    // with each signal that contributed, using the live policy from Dataverse. Answers "why did (not) these link?".
    using var client = Connect();
    var key = Str("customer") ?? throw new ArgumentException("--customer is required, e.g. contact:<guid without dashes>");
    var modelId = Str("model") ?? "aoai:text-embedding-3-small:512";
    var repo = new DataverseLedgerRepository(client, loggerFactory.CreateLogger<DataverseLedgerRepository>(), modelId);
    var (policy, _) = await new DataverseLedgerConfigurationProvider(client, loggerFactory.CreateLogger<DataverseLedgerConfigurationProvider>(), modelId).GetAsync();
    var history = (await repo.LoadCustomerHistoryAsync(key, DateTimeOffset.UtcNow.AddDays(-Int("days", 30)), 250)).OrderBy(i => i.StartedOn).ToList();
    var scorer = new Vrl.Core.Similarity.SameIssueScorer(policy);
    Console.WriteLine($"  {history.Count} interaction(s) for {key}; threshold {policy.SameIssueThreshold:F2}; semantic floor {policy.Weights.SemanticFloor:F2}");
    foreach (var i in history)
        Console.WriteLine($"  [{i.SourceRecordId[..8]}] {i.StartedOn:u} {i.HandlingMode,-12} {i.Persisted?.Verdict} refs={string.Join(",", Vrl.Core.Similarity.ReferenceExtractor.Default.Extract(i.Summary))} emb={(i.Embedding?.Length ?? 0)}/{i.EmbeddingModel}");
    for (var b = 1; b < history.Count; b++)
        for (var a = 0; a < b; a++)
        {
            var r = scorer.Score(history[a], history[b]);
            Console.WriteLine($"\n  {history[b].SourceRecordId[..8]} vs earlier {history[a].SourceRecordId[..8]}: score {r.Score:F3} same-issue={r.IsSameIssue}");
            foreach (var sgn in r.Signals) Console.WriteLine($"      {sgn.Signal,-16} {sgn.Contribution,7:F3}  {sgn.Detail}");
            if (r.Signals.Count == 0) Console.WriteLine("      (no signals)");
        }
}

void Diag()
{
    // Live-pipeline diagnostics inside Dataverse: recent conversations and the service-endpoint system jobs.
    using var client = Connect();
    Console.WriteLine("\n  Recent conversations (msdyn_ocliveworkitem)");
    try
    {
        var convs = client.RetrieveMultiple(new QueryExpression("msdyn_ocliveworkitem")
        {
            ColumnSet = new ColumnSet("statecode", "statuscode", "msdyn_customer", "createdon", "modifiedon", "msdyn_channel"),
            TopCount = 5,
            Orders = { new OrderExpression("createdon", OrderType.Descending) },
        }).Entities;
        foreach (var c in convs)
        {
            c.FormattedValues.TryGetValue("statuscode", out var st);
            c.FormattedValues.TryGetValue("msdyn_channel", out var ch);
            var cust = c.GetAttributeValue<EntityReference>("msdyn_customer");
            Console.WriteLine($"    {c.Id}  status={st ?? "?"}({c.GetAttributeValue<OptionSetValue>("statuscode")?.Value})  channel={ch}  " +
                              $"customer={(cust is null ? "(none)" : cust.LogicalName + ":" + cust.Name)}  modified={c.GetAttributeValue<DateTime>("modifiedon"):u}");
        }
        if (convs.Count == 0) Console.WriteLine("    (none)");
    }
    catch (Exception ex) { Console.WriteLine($"    could not read conversations: {ex.Message}"); }

    Console.WriteLine("\n  Service endpoint system jobs (last 2 hours)");
    var jobs = client.RetrieveMultiple(new QueryExpression("asyncoperation")
    {
        ColumnSet = new ColumnSet("name", "statuscode", "message", "friendlymessage", "createdon", "completedon"),
        TopCount = 10,
        Criteria =
        {
            Conditions =
            {
                new ConditionExpression("name", ConditionOperator.Like, "%VRL%"),
                new ConditionExpression("createdon", ConditionOperator.LastXHours, 2),
            },
        },
        Orders = { new OrderExpression("createdon", OrderType.Descending) },
    }).Entities;
    foreach (var j in jobs)
    {
        j.FormattedValues.TryGetValue("statuscode", out var st);
        var msg = j.GetAttributeValue<string>("friendlymessage") ?? j.GetAttributeValue<string>("message");
        Console.WriteLine($"    {j.GetAttributeValue<DateTime>("createdon"):u}  {st}  {(msg is null ? "" : msg.Split('\n')[0])}");
    }
    if (jobs.Count == 0) Console.WriteLine("    (none) -> the step has not fired for any conversation yet");
    Console.WriteLine();
}

async Task Promote()
{
    // ALM path for environments where metadata-API table creation times out (e.g. large Contact Center orgs):
    // export the solution from the environment where it was provisioned and import it asynchronously into the target.
    // Unmanaged import is used so it merges with any partially-provisioned unmanaged components in the target.
    var targetUrl = Str("target-url") ?? throw new ArgumentException("--target-url is required");
    using var source = Connect();
    log.LogInformation("Exporting solution {Solution} from source...", S.SolutionUniqueName);
    var export = WithSolutionLockRetry(() => (Microsoft.Crm.Sdk.Messages.ExportSolutionResponse)source.Execute(new Microsoft.Crm.Sdk.Messages.ExportSolutionRequest
    {
        SolutionName = S.SolutionUniqueName,
        Managed = false,
    }), "export");
    var zipPath = Path.Combine(Path.GetTempPath(), $"{S.SolutionUniqueName}_{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
    await File.WriteAllBytesAsync(zipPath, export.ExportSolutionFile);
    log.LogInformation("Exported {Bytes:N0} bytes to {Path}", export.ExportSolutionFile.Length, zipPath);

    Microsoft.PowerPlatform.Dataverse.Client.ServiceClient.MaxConnectionTimeout = TimeSpan.FromMinutes(10);
    using var target = DataverseClientFactory.Create(new Uri(targetUrl), CliCredential(Str("tenant"), Str("subscription")), loggerFactory.CreateLogger("dataverse"));
    var resp = WithSolutionLockRetry(() => (Microsoft.Crm.Sdk.Messages.ImportSolutionAsyncResponse)target.Execute(new Microsoft.Crm.Sdk.Messages.ImportSolutionAsyncRequest
    {
        CustomizationFile = export.ExportSolutionFile,
        OverwriteUnmanagedCustomizations = true,
        PublishWorkflows = true,
    }), "import");
    var asyncId = resp.AsyncOperationId;
    log.LogInformation("Import queued as async operation {Id}", asyncId);

    for (var i = 0; i < 180; i++)   // up to 30 minutes
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        var op = target.Retrieve("asyncoperation", asyncId, new ColumnSet("statecode", "statuscode", "message", "friendlymessage"));
        var state = op.GetAttributeValue<OptionSetValue>("statecode")?.Value;
        var status = op.GetAttributeValue<OptionSetValue>("statuscode")?.Value;
        if (i % 3 == 0) log.LogInformation("  import status {Status}", status);
        if (state != 3) continue; // 3 = Completed
        if (status == 30) { log.LogInformation("Solution imported into {Target}", targetUrl); break; }   // Succeeded
        var msg = op.GetAttributeValue<string>("friendlymessage") ?? op.GetAttributeValue<string>("message");
        throw new InvalidOperationException($"Solution import ended with status {status}: {msg}");
    }

    log.LogInformation("Publishing customizations in target...");
    WithSolutionLockRetry(() => target.Execute(new Microsoft.Crm.Sdk.Messages.PublishAllXmlRequest()), "publish");
}

void RegisterEndpoint()
{
    // Registers the Service Bus service endpoint and an async post-operation step on msdyn_ocliveworkitem Update
    // (filtered on statuscode), so closed conversations flow to the ledger. Equivalent to the Plugin Registration Tool.
    // The SAS key is read from VRL_SB_SAS_KEY so it never appears on the command line or in logs.
    using var client = Connect();
    var ns = Str("sb-namespace") ?? throw new ArgumentException("--sb-namespace is required");
    var queue = Str("queue", "vrl-dataverse-events")!;
    var keyName = Str("sas-key-name", "dataverse-send")!;
    var sasKey = Environment.GetEnvironmentVariable("VRL_SB_SAS_KEY");
    if (string.IsNullOrWhiteSpace(sasKey)) throw new ArgumentException("Environment variable VRL_SB_SAS_KEY is not set.");
    const string endpointName = "VRL Dataverse Events";
    const string entity = "msdyn_ocliveworkitem";

    var existing = client.RetrieveMultiple(new QueryExpression("serviceendpoint")
    {
        ColumnSet = new ColumnSet("serviceendpointid"),
        Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, endpointName) } },
    }).Entities.FirstOrDefault();

    var endpoint = new Entity("serviceendpoint")
    {
        ["name"] = endpointName,
        ["description"] = "Verified Resolution Ledger: closed conversations to Azure Service Bus.",
        ["contract"] = new OptionSetValue(6),        // Queue (Persistent); plain Queue (2) is rejected by current Dataverse
        ["authtype"] = new OptionSetValue(2),        // SAS Key
        ["messageformat"] = new OptionSetValue(2),   // Json
        ["namespaceformat"] = new OptionSetValue(2), // Namespace Address
        ["namespaceaddress"] = $"sb://{ns}.servicebus.windows.net/",
        ["path"] = queue,
        ["saskeyname"] = keyName,
        ["saskey"] = sasKey,
        ["userclaim"] = new OptionSetValue(1),       // None
        ["connectionmode"] = new OptionSetValue(1),  // Normal
    };
    Guid endpointId;
    if (existing is null) { endpointId = client.Create(endpoint); log.LogInformation("Created service endpoint {Id}", endpointId); }
    else { endpoint.Id = endpointId = existing.Id; client.Update(endpoint); log.LogInformation("Updated service endpoint {Id}", endpointId); }
    // Deliberately NOT added to the ledger solution: the endpoint carries this environment's Service Bus namespace.

    var messageId = client.RetrieveMultiple(new QueryExpression("sdkmessage")
    {
        ColumnSet = new ColumnSet("sdkmessageid"),
        Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, "Update") } },
    }).Entities.Single().Id;

    var filter = client.RetrieveMultiple(new QueryExpression("sdkmessagefilter")
    {
        ColumnSet = new ColumnSet("sdkmessagefilterid"),
        Criteria =
        {
            Conditions =
            {
                new ConditionExpression("sdkmessageid", ConditionOperator.Equal, messageId),
                new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, entity),
            },
        },
    }).Entities.FirstOrDefault() ?? throw new InvalidOperationException($"No Update message filter for {entity}; is Contact Center installed in this environment?");

    var stepName = $"VRL: {entity} Update (statuscode) -> Service Bus";
    var step = client.RetrieveMultiple(new QueryExpression("sdkmessageprocessingstep")
    {
        ColumnSet = new ColumnSet("sdkmessageprocessingstepid"),
        Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, stepName) } },
    }).Entities.FirstOrDefault();

    var stepEntity = new Entity("sdkmessageprocessingstep")
    {
        ["name"] = stepName,
        ["description"] = "Sends closed conversations to the Verified Resolution Ledger.",
        ["eventhandler"] = new EntityReference("serviceendpoint", endpointId),
        ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
        ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filter.Id),
        ["mode"] = new OptionSetValue(1),                 // Asynchronous
        ["stage"] = new OptionSetValue(40),               // Post-operation
        ["rank"] = 1,
        ["supporteddeployment"] = new OptionSetValue(0),  // Server only
        ["filteringattributes"] = "statuscode",
        ["asyncautodelete"] = true,
    };
    Guid stepId;
    if (step is null) { stepId = client.Create(stepEntity); log.LogInformation("Registered step {Name}", stepName); }
    else { stepEntity.Id = stepId = step.Id; client.Update(stepEntity); log.LogInformation("Updated step {Name}", stepName); }
    // Deliberately NOT added to the ledger solution: the step belongs to the environment-specific endpoint.
}

void Show()
{
    // Lists ledger rows for one source system (e.g. the smoke test) with their verdicts.
    using var client = Connect();
    var source = Str("source") ?? "smoketest";
    var rows = client.RetrieveMultiple(new QueryExpression(S.Interaction.Table)
    {
        ColumnSet = new ColumnSet(S.Interaction.SourceRecordId, S.Interaction.StartedOn, S.Interaction.Verdict, S.Interaction.VerdictCode,
            S.Interaction.EpisodeKey, S.Interaction.EmbeddingModel, S.Interaction.VerdictFinal),
        Criteria = { Conditions = { new ConditionExpression(S.Interaction.SourceSystem, ConditionOperator.Equal, source) } },
        Orders = { new OrderExpression(S.Interaction.StartedOn, OrderType.Ascending) },
    }).Entities;
    Console.WriteLine($"  {rows.Count} row(s) for source '{source}'");
    foreach (var e in rows)
    {
        var verdict = e.GetAttributeValue<OptionSetValue>(S.Interaction.Verdict)?.Value is { } v ? ((OutcomeVerdict)v).ToString() : "(not evaluated)";
        Console.WriteLine($"  {e.GetAttributeValue<string>(S.Interaction.SourceRecordId),-28} {verdict,-22} {e.GetAttributeValue<string>(S.Interaction.VerdictCode),-18} " +
                          $"final={e.GetAttributeValue<bool>(S.Interaction.VerdictFinal),-5} model={e.GetAttributeValue<string>(S.Interaction.EmbeddingModel)}");
    }
}

void AssignAppUser()
{
    // Equivalent of `pac admin assign-user --application-user`, via the SDK (no pac dependency).
    using var client = Connect();
    var appId = Guid.Parse(Str("app-id") ?? throw new ArgumentException("--app-id is required"));
    var roleNames = (Str("roles") ?? "Basic User,VRL Ledger Service").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    var rootBu = client.RetrieveMultiple(new QueryExpression("businessunit")
    {
        ColumnSet = new ColumnSet("businessunitid"),
        Criteria = { Conditions = { new ConditionExpression("parentbusinessunitid", ConditionOperator.Null) } },
    }).Entities.First().Id;

    var user = client.RetrieveMultiple(new QueryExpression("systemuser")
    {
        ColumnSet = new ColumnSet("systemuserid", "isdisabled"),
        Criteria = { Conditions = { new ConditionExpression("applicationid", ConditionOperator.Equal, appId) } },
    }).Entities.FirstOrDefault();

    var userId = user?.Id ?? client.Create(new Entity("systemuser")
    {
        ["applicationid"] = appId,
        ["businessunitid"] = new EntityReference("businessunit", rootBu),
    });
    log.LogInformation(user is null ? "Created application user {Id}" : "Application user exists {Id}", userId);

    var existingRoles = client.RetrieveMultiple(new FetchExpression($"""
        <fetch><entity name="role"><attribute name="name" />
          <link-entity name="systemuserroles" from="roleid" to="roleid" intersect="true">
            <filter><condition attribute="systemuserid" operator="eq" value="{userId}" /></filter>
          </link-entity></entity></fetch>
        """)).Entities.Select(e => e.GetAttributeValue<string>("name")).ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var name in roleNames)
    {
        if (existingRoles.Contains(name)) { log.LogInformation("Role already assigned: {Role}", name); continue; }
        var role = client.RetrieveMultiple(new QueryExpression("role")
        {
            ColumnSet = new ColumnSet("roleid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, name),
                    new ConditionExpression("businessunitid", ConditionOperator.Equal, rootBu),
                },
            },
        }).Entities.FirstOrDefault() ?? throw new InvalidOperationException($"Security role '{name}' not found in the root business unit.");
        client.Associate("systemuser", userId, new Relationship("systemuserroles_association"),
            new EntityReferenceCollection { new EntityReference("role", role.Id) });
        log.LogInformation("Assigned role {Role}", name);
    }
}

void Verify()
{
    // Post-deployment evidence straight from Dataverse: row counts by verdict and handling mode, plus episodes.
    using var client = Connect();
    Console.WriteLine();
    foreach (var (attr, type) in new[] { (S.Interaction.Verdict, typeof(OutcomeVerdict)), (S.Interaction.HandlingMode, typeof(HandlingMode)) })
    {
        var fetch = $"""
            <fetch aggregate="true">
              <entity name="{S.Interaction.Table}">
                <attribute name="{attr}" groupby="true" alias="k" />
                <attribute name="{S.Interaction.Id}" aggregate="count" alias="n" />
              </entity>
            </fetch>
            """;
        Console.WriteLine($"  {attr}");
        foreach (var e in client.RetrieveMultiple(new FetchExpression(fetch)).Entities)
        {
            var k = (e.GetAttributeValue<AliasedValue>("k")?.Value as OptionSetValue)?.Value;
            var n = e.GetAttributeValue<AliasedValue>("n")?.Value;
            Console.WriteLine($"    {(k is { } v ? Enum.GetName(type, v) : "(empty)"),-24} {n,8}");
        }
    }
    var episodes = client.RetrieveMultiple(new FetchExpression(
        $"<fetch aggregate=\"true\"><entity name=\"{S.Episode.Table}\"><attribute name=\"{S.Episode.Id}\" aggregate=\"count\" alias=\"n\" /></entity></fetch>"))
        .Entities.FirstOrDefault()?.GetAttributeValue<AliasedValue>("n")?.Value;
    Console.WriteLine($"  episodes                   {episodes,8}");
    var webResources = client.RetrieveMultiple(new QueryExpression("webresource")
    {
        ColumnSet = new ColumnSet("name"),
        Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.BeginsWith, "bpc_/vrl/") } },
    }).Entities.Select(e => e.GetAttributeValue<string>("name"));
    Console.WriteLine($"  web resources              {string.Join(", ", webResources)}");
    Console.WriteLine();
}

void DeployWebResources()
{
    using (var probe = Connect())
    {
        if (LedgerSolutionIsManaged(probe))
        {
            log.LogInformation("Managed solution {Solution} is installed: web resources come from the package; upload skipped", S.SolutionUniqueName);
            return;
        }
    }
    // Uploads webresources/dist/** as bpc_/... web resources, adds them to the solution and publishes them.
    using var client = Connect();
    var root = Str("path") ?? Path.Combine("webresources", "dist");
    if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"{root} not found – run: node webresources/build.mjs");
    var ids = new List<Guid>();

    foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
    {
        var name = Path.GetRelativePath(root, file).Replace('\\', '/');           // e.g. bpc_/vrl/dashboard.html
        var type = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" or ".htm" => 1, ".css" => 2, ".js" => 3, ".png" => 5, ".svg" => 11, _ => 0,
        };
        if (type == 0) continue;

        var existing = client.RetrieveMultiple(new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("webresourceid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, name) } },
        }).Entities.FirstOrDefault();

        var wr = new Entity("webresource")
        {
            ["content"] = Convert.ToBase64String(File.ReadAllBytes(file)),
            ["displayname"] = $"VRL – {Path.GetFileNameWithoutExtension(file)}",
        };
        Guid id;
        if (existing is null)
        {
            wr["name"] = name;
            wr["webresourcetype"] = new OptionSetValue(type);
            id = client.Create(wr);
        }
        else
        {
            wr.Id = id = existing.Id;
            client.Update(wr);
        }
        client.Execute(new Microsoft.Crm.Sdk.Messages.AddSolutionComponentRequest
        {
            ComponentType = 61, ComponentId = id, SolutionUniqueName = S.SolutionUniqueName,
        });
        ids.Add(id);
        log.LogInformation("Uploaded {Name}", name);
    }

    var xml = $"<importexportxml><webresources>{string.Concat(ids.Select(i => $"<webresource>{{{i}}}</webresource>"))}</webresources></importexportxml>";
    WithSolutionLockRetry(() => client.Execute(new Microsoft.Crm.Sdk.Messages.PublishXmlRequest { ParameterXml = xml }), "publish");
    log.LogInformation("Published {Count} web resource(s)", ids.Count);
}

// ---------------------------------------------------------------------------------------------------------------------

// Same settings as the Function App (Vrl__Adapter__*), so CLI replays map conversations exactly like the pipeline.
// Deploy scripts set them from the "adapter" section of deploy/config.json.
OmnichannelAdapterOptions AdapterOptions()
{
    var o = new OmnichannelAdapterOptions();
    if (bool.TryParse(Environment.GetEnvironmentVariable("Vrl__Adapter__IdentifyFromSelfDeclaredEmail"), out var self)) o.IdentifyFromSelfDeclaredEmail = self;
    if (double.TryParse(Environment.GetEnvironmentVariable("Vrl__Adapter__DigitalCreditsPerBotConversation"), NumberStyles.Float, CultureInfo.InvariantCulture, out var digital)) o.DigitalCreditsPerBotConversation = digital;
    if (double.TryParse(Environment.GetEnvironmentVariable("Vrl__Adapter__VoiceCreditsPerBotMinute"), NumberStyles.Float, CultureInfo.InvariantCulture, out var voice)) o.VoiceCreditsPerBotMinute = voice;
    return o;
}

// Console output never shows the contact's raw phone number (it is only carried for redaction) or the vector.
// Dataverse serialises solution operations: a publish, import or export fails while another one (often a
// background install of a Microsoft solution on trials) holds the lock. That clears by itself, so wait and retry.
static bool IsSolutionLock(Exception ex)
{
    for (var e = ex; e is not null; e = e.InnerException)
    {
        var m = e.Message ?? "";
        if (m.Contains("because there is another", StringComparison.OrdinalIgnoreCase)
            || m.Contains("installation or removal of another solution", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Cannot start the requested operation", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

static T WithSolutionLockRetry<T>(Func<T> operation, string what)
{
    int[] SolutionLockBackoffSeconds = { 30, 60, 120, 240 };
    for (var attempt = 0; ; attempt++)
    {
        try { return operation(); }
        catch (Exception ex) when (attempt < SolutionLockBackoffSeconds.Length && IsSolutionLock(ex))
        {
            var wait = SolutionLockBackoffSeconds[attempt];
            Console.WriteLine($"  Dataverse is busy with another solution operation; retrying the {what} in {wait}s " +
                              $"(attempt {attempt + 2} of {SolutionLockBackoffSeconds.Length + 1})...");
            Thread.Sleep(TimeSpan.FromSeconds(wait));
        }
    }
}

static Interaction ForDisplay(Interaction i) => i with
{
    Embedding = null,
    Customer = i.Customer with { Phone = i.Customer.Phone is { Length: > 2 } p ? "***" + p[^2..] : i.Customer.Phone, Email = i.Customer.Email is null ? null : "***" },
};

Microsoft.PowerPlatform.Dataverse.Client.ServiceClient Connect()
{
    var url = Str("url") ?? Environment.GetEnvironmentVariable("VRL_DATAVERSE_URL")
        ?? throw new ArgumentException("--url https://<org>.crm.dynamics.com is required");
    var tenant = Str("tenant") ?? Environment.GetEnvironmentVariable("VRL_DATAVERSE_TENANT");
    var subscription = Str("subscription");
    log.LogInformation("Connecting to {Url} (tenant {Tenant}, CLI subscription {Sub})...", url, tenant ?? "default", subscription ?? "current");
    return DataverseClientFactory.Create(new Uri(url), CliCredential(tenant, subscription), loggerFactory.CreateLogger("dataverse"));
}

// Explicit Azure CLI credential per side: Dataverse and Azure can live in different Entra tenants with different
// signed-in accounts. Pinning a subscription makes the CLI use the account that owns it, independent of which
// subscription is currently active. (--subscription and --tenant are mutually exclusive in the CLI.)
static Azure.Core.TokenCredential CliCredential(string? tenant, string? subscription = null)
{
    if (string.IsNullOrWhiteSpace(tenant) && string.IsNullOrWhiteSpace(subscription))
        return new Azure.Identity.DefaultAzureCredential();
    var o = new Azure.Identity.AzureCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) };
    if (!string.IsNullOrWhiteSpace(subscription)) o.Subscription = subscription;
    else o.TenantId = tenant;
    return new Azure.Identity.AzureCliCredential(o);
}

IEmbeddingProvider Embeddings()
{
    var endpoint = Str("aoai-endpoint");
    var deployment = Str("aoai-deployment");
    if (endpoint is not null && deployment is not null)
        return new AzureOpenAIEmbeddingProvider(new Uri(endpoint), deployment, Int("aoai-dimensions", 512),
            CliCredential(Str("azure-tenant"), Str("azure-subscription")), Str("aoai-model", "text-embedding-3-small"));
    return new LocalHashingEmbeddingProvider();
}

void PrintKpis(LedgerKpis k, AccuracyReport a)
{
    var ci = CultureInfo.InvariantCulture;
    Console.WriteLine();
    Console.WriteLine("  VERIFIED RESOLUTION LEDGER — synthetic run");
    Console.WriteLine("  ─────────────────────────────────────────────────────────────");
    Console.WriteLine($"  Interactions                 {k.Interactions,8}   (pending {k.PendingInteractions})");
    Console.WriteLine($"  Native bot deflection        {k.NativeDeflectionRate.ToString("P1", ci),8}");
    Console.WriteLine($"  Verified containment         {k.VerifiedContainmentRate.ToString("P1", ci),8}");
    Console.WriteLine($"  Containment gap              {k.ContainmentGapPoints,6:0.0} pts");
    Console.WriteLine($"  Repeat-contact rate          {k.RepeatContactRate.ToString("P1", ci),8}");
    Console.WriteLine($"  Interaction-derived FCR      {k.InteractionFcr.ToString("P1", ci),8}");
    Console.WriteLine($"  Cost per contact             {k.CostPerContact,8:0.00} EUR");
    Console.WriteLine($"  Cost per resolved outcome    {k.CostPerResolvedOutcome,8:0.00} EUR");
    Console.WriteLine($"  Spend on repeated contacts   {k.CostOfRepeats,8:0.00} EUR");
    Console.WriteLine();
    Console.WriteLine("  Accuracy vs ground truth");
    Console.WriteLine($"  Repeat detection precision   {a.RepeatPrecision.ToString("P1", ci),8}");
    Console.WriteLine($"  Repeat detection recall      {a.RepeatRecall.ToString("P1", ci),8}   (in-scope; {a.FalseNegatives} missed)");
    Console.WriteLine($"  Out-of-scope repeats         {a.OutOfScopeRepeats,8}   (anonymous or beyond window)");
    Console.WriteLine($"  True bot containment         {a.TrueContainmentRate.ToString("P1", ci),8}");
    Console.WriteLine();
    Console.WriteLine("  Top bot topics by false containment");
    foreach (var b in k.ByBotTopic.OrderByDescending(b => b.FalseContainment).Take(5))
        Console.WriteLine($"    {b.Value,-26} {b.FalseContainment,4} of {b.Interactions,4}");
    Console.WriteLine();
}

async Task WriteCsv(string path, IReadOnlyList<Interaction> items, Vrl.Core.Engine.EvaluationResult result)
{
    var ev = result.Interactions.ToDictionary(e => e.InteractionId);
    var sb = new StringBuilder("id,startedOn,channel,handlingMode,nativeBotOutcome,intent,botTopic,queue,verdict,verdictCode,isFinal,episodeKey,predecessorId,successorId,costAi,costTelephony,costLabor,costTotal\n");
    foreach (var i in items.OrderBy(i => i.StartedOn))
    {
        if (!ev.TryGetValue(i.Id, out var e)) continue;
        sb.AppendLine(string.Join(",",
            i.Id, i.StartedOn.ToString("o"), i.Channel, i.HandlingMode, i.NativeBotOutcome, Csv(i.IntentCode), Csv(i.BotTopic), Csv(i.QueueName),
            e.Verdict, e.Reason.Code, e.IsFinal, e.EpisodeKey, e.PredecessorId, e.SuccessorId,
            e.Cost.Ai.ToString(CultureInfo.InvariantCulture), e.Cost.Telephony.ToString(CultureInfo.InvariantCulture),
            e.Cost.Labor.ToString(CultureInfo.InvariantCulture), e.Cost.Total.ToString(CultureInfo.InvariantCulture)));
    }
    await File.WriteAllTextAsync(path, sb.ToString());
    static string Csv(string? s) => s is null ? "" : $"\"{s.Replace("\"", "\"\"")}\"";
}

async Task WriteDemoJson(string path, IReadOnlyList<Interaction> items, Vrl.Core.Engine.EvaluationResult result, DateTimeOffset asOf)
{
    // Same row shape the dashboard web resource builds from the Dataverse Web API.
    var ev = result.Interactions.ToDictionary(e => e.InteractionId);
    var rows = items.Where(i => ev.ContainsKey(i.Id)).OrderBy(i => i.StartedOn).Select(i =>
    {
        var e = ev[i.Id];
        return new
        {
            id = i.Id, customer = i.Customer.ResolveKey().Key, startedOn = i.StartedOn, endedOn = i.EndedOn,
            channel = i.Channel.ToString(), mode = i.HandlingMode.ToString(), native = i.NativeBotOutcome.ToString(),
            intent = i.IntentCode, topic = i.BotTopic, queue = i.QueueName, verdict = e.Verdict.ToString(), code = e.Reason.Code,
            reason = e.Reason.Message, final = e.IsFinal, episode = e.EpisodeKey, successor = e.SuccessorId, pred = e.PredecessorId,
            score = e.PredecessorScore, cost = e.Cost.Total, costAi = e.Cost.Ai, costTel = e.Cost.Telephony, costLabor = e.Cost.Labor,
            summary = i.Summary,
        };
    });
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { asOf, currency = "EUR", rows },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
}

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        d[key] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    }
    return d;
}

string? Str(string key, string? fallback = null) => opts.TryGetValue(key, out var v) ? v : fallback;
int Int(string key, int fallback) => opts.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

public partial class Program
{
    internal static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

// ---------------------------------------------------------------------------------------------------------------------
// Solution component inspection (shared by solution-report and package)
internal static class SolutionInspector
{

    public static List<SolutionComponentInfo> Components(Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client, string solutionName)
    {
        var sol = client.RetrieveMultiple(new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid", "version", "ismanaged"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, solutionName) } },
        }).Entities.FirstOrDefault() ?? throw new InvalidOperationException($"Solution {solutionName} not found.");
        Console.WriteLine($"  Solution {solutionName} {sol.GetAttributeValue<string>("version")} managed={sol.GetAttributeValue<bool>("ismanaged")}");

        var rows = client.RetrieveMultiple(new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("componenttype", "objectid", "rootcomponentbehavior"),
            Criteria = { Conditions = { new ConditionExpression("solutionid", ConditionOperator.Equal, sol.Id) } },
        }).Entities;

        // Solution-aware tables (bots, tab templates, ...) register their own component type numbers.
        var defs = client.RetrieveMultiple(new QueryExpression("solutioncomponentdefinition")
        {
            ColumnSet = new ColumnSet("solutioncomponenttype", "primaryentityname"),
        }).Entities.GroupBy(d => d.GetAttributeValue<int>("solutioncomponenttype"))
          .ToDictionary(g => g.Key, g => g.First().GetAttributeValue<string>("primaryentityname"));

        var list = new List<SolutionComponentInfo>();
        foreach (var r in rows)
        {
            var type = r.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
            var id = r.GetAttributeValue<Guid>("objectid");
            var behavior = r.GetAttributeValue<OptionSetValue>("rootcomponentbehavior")?.Value ?? 0;
            string typeName = type switch
            {
                1 => "Table", 2 => "Column", 9 => "Choice", 10 => "Relationship", 14 => "Key", 20 => "Security role",
                26 => "View", 29 => "Process", 59 => "Chart", 60 => "Form", 61 => "Web resource", 80 => "App",
                62 => "Site map", 91 => "Plug-in assembly", 92 => "Plug-in step", 95 => "Service endpoint",
                _ => defs.TryGetValue(type, out var e) ? e ?? $"type {type}" : $"type {type}",
            };
            string name;
            try { name = ComponentName(client, type, id, defs); }
            catch (Exception ex) { name = $"{id} ({ex.GetType().Name})"; }
            list.Add(new SolutionComponentInfo(type, typeName, id, name, behavior));
        }
        return list.OrderBy(c => c.Type).ThenBy(c => c.Name).ToList();
    }

    public static string ComponentName(Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client, int type, Guid id, Dictionary<int, string> defs)
    {
        switch (type)
        {
            case 1: return ((Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest { MetadataId = id, EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity })).EntityMetadata.LogicalName;
            case 2:
                var a = ((Microsoft.Xrm.Sdk.Messages.RetrieveAttributeResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveAttributeRequest { MetadataId = id })).AttributeMetadata;
                return $"{a.EntityLogicalName}.{a.LogicalName}";
            case 9: return ((Microsoft.Xrm.Sdk.Messages.RetrieveOptionSetResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveOptionSetRequest { MetadataId = id })).OptionSetMetadata.Name;
            case 10: return ((Microsoft.Xrm.Sdk.Messages.RetrieveRelationshipResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveRelationshipRequest { MetadataId = id })).RelationshipMetadata.SchemaName;
            case 20: return client.Retrieve("role", id, new ColumnSet("name")).GetAttributeValue<string>("name");
            case 26: return client.Retrieve("savedquery", id, new ColumnSet("name", "returnedtypecode")) is var v ? $"{v.GetAttributeValue<string>("returnedtypecode")}: {v.GetAttributeValue<string>("name")}" : "";
            case 60: return client.Retrieve("systemform", id, new ColumnSet("name", "objecttypecode")) is var f ? $"{f.GetAttributeValue<string>("objecttypecode")}: {f.GetAttributeValue<string>("name")}" : "";
            case 61: return client.Retrieve("webresource", id, new ColumnSet("name")).GetAttributeValue<string>("name");
            case 92: return client.Retrieve("sdkmessageprocessingstep", id, new ColumnSet("name")).GetAttributeValue<string>("name");
            case 95: return client.Retrieve("serviceendpoint", id, new ColumnSet("name")).GetAttributeValue<string>("name");
            default:
                if (defs.TryGetValue(type, out var entity) && entity is not null)
                {
                    var md = ((Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest { LogicalName = entity, EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity })).EntityMetadata;
                    return client.Retrieve(entity, id, new ColumnSet(md.PrimaryNameAttribute)).GetAttributeValue<string>(md.PrimaryNameAttribute) ?? id.ToString();
                }
                return id.ToString();
        }
    }
}

internal sealed record SolutionComponentInfo(int Type, string TypeName, Guid Id, string Name, int Behavior);

/// <summary>What may ship in the distributable solution, and a check of an exported zip against it.</summary>
internal static class PackagePolicy
{
    // Tables, columns, choices, relationships, keys, roles, views, charts, forms, web resources.
    private static readonly HashSet<int> AllowedTypes = [1, 2, 9, 10, 14, 20, 26, 59, 60, 61];

    public static bool IsAllowed(SolutionComponentInfo c) => AllowedTypes.Contains(c.Type) && c.Type switch
    {
        1 => c.Name.StartsWith("bpc_", StringComparison.Ordinal),                 // no layers on Microsoft's tables
        2 => c.Name.StartsWith("bpc_", StringComparison.Ordinal),                 // columns on our tables only
        61 => c.Name.StartsWith("bpc_/vrl/", StringComparison.Ordinal),
        20 => c.Name.StartsWith("VRL ", StringComparison.Ordinal),
        _ => true,
    };

    /// <summary>Inspects solution.xml and customizations.xml of an exported zip.</summary>
    public static List<string> Verify(string zipPath)
    {
        var problems = new List<string>();
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }

        var solution = System.Xml.Linq.XDocument.Parse(Read("solution.xml"));
        foreach (var rc in solution.Descendants("RootComponent"))
        {
            var type = (int?)rc.Attribute("type") ?? 0;
            var schema = (string?)rc.Attribute("schemaName") ?? (string?)rc.Attribute("id") ?? "";
            if (!AllowedTypes.Contains(type)) problems.Add($"root component type {type} ({schema})");
            else if (type == 1 && !schema.StartsWith("bpc_", StringComparison.Ordinal)) problems.Add($"system table {schema}");
        }
        var custom = Read("customizations.xml");
        foreach (var marker in new[] { "<ServiceEndpoint", "<SdkMessageProcessingStep", "<bots>", "<bot ", "<botcomponent", "SharedAccessKey", "servicebus.windows.net" })
            if (custom.Contains(marker, StringComparison.OrdinalIgnoreCase)) problems.Add($"customizations.xml contains '{marker}'");
        var entities = System.Xml.Linq.XDocument.Parse(custom).Descendants("Entity").Select(e => (string?)e.Element("Name")).Where(n => n is not null);
        foreach (var n in entities)
            if (!n!.StartsWith("bpc_", StringComparison.Ordinal)) problems.Add($"table definition for {n}");
        return problems;
    }
}

