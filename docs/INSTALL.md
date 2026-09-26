# Installing the Verified Resolution Ledger

Two ways to install, one tool (`Deploy.cmd` / `deploy\Deploy-Ledger.ps1`), driven by `deploy\config.json`.

| Mode | For | Dataverse components come from | Synthetic data |
|---|---|---|---|
| **managed** (recommended) | Test, acceptance and production environments | The released **managed solution** (`VerifiedResolutionLedger_<version>_managed.zip`) | Never, unless you ask for it explicitly |
| **source** | Development and contribution | Code (`vrl provision`, unmanaged) | Seeded by default, for the dashboard |

Never mix the two in one environment. `Deploy` refuses to install the managed package over an unmanaged ledger solution,
and in a managed environment it never changes the schema, the roles or the web resources. Editing managed components
creates unmanaged layers that block future upgrades.

## 1. Prerequisites

| Area | Requirement |
|---|---|
| Dataverse | Dynamics 365 **Contact Center** or **Customer Service with Omnichannel**. The managed package references the Case table, and the live adapter reads Omnichannel conversations. Your account must be **System Administrator** there. |
| Azure | A subscription and an existing resource group where you are **Owner** or **User Access Administrator** (the deployment creates managed-identity role assignments). Dataverse and Azure may be in **different Entra tenants**. |
| Embeddings (optional, recommended) | An Azure OpenAI or Foundry account in the same resource group. The deployment adds a `text-embedding-3-small` deployment and grants the Function access. Without it, a built-in local embedding is used, with weaker paraphrase matching. |
| Workstation | Windows PowerShell 5.1 or PowerShell 7, .NET 8 SDK or later, a current Azure CLI (tested with 2.82), and Node.js 18+ (source mode only). |

## 2. Configure

```powershell
copy deploy\config.example.json deploy\config.json   # git-ignored: never commit it
```

Fill in `dataverse.url`, both tenant ids, `azure.subscriptionId`, `azure.resourceGroup`, and optionally `openAi.accountName`.
Keep `"install": "managed"`. Use one config file per environment and switch between them with
`set VRL_CONFIG=deploy\config.<env>.json`.

## 3. Install (managed)

1. Download `VerifiedResolutionLedger_<version>_managed.zip` from the GitHub release into `artifacts\solution\`
   (or set `solution.zip` in the config).
2. Run **`Deploy.cmd`**. Browser sign-in windows open once for each tenant.

| Step | What it does |
|---|---|
| `signin` | Azure CLI sign-in to the Dataverse tenant and the Azure tenant |
| `solution` | Imports the managed solution asynchronously. An older managed version is **upgraded** (holding solution, then DeleteAndPromote); the same or a newer version is skipped |
| `dataverse` | Seeds the configuration rows (repeat-window policy, cost rates). Schema provisioning is skipped because the package delivers it |
| `appreg` | App registration `vrl-ledger-dataverse` in the Dataverse tenant: the Function's identity for Dataverse |
| `infra` | Bicep: Flex Consumption Function App, Service Bus (session queue), Storage (no shared keys), Key Vault (RBAC), Application Insights, user-assigned managed identity, embedding deployment |
| `secret` | Creates the app registration's client secret and stores it in Key Vault through a temporary file that is deleted immediately (never printed). `-RotateSecret` adds a new one; delete old ones with `az ad app credential delete` |
| `code` | Builds and deploys the Function App |
| `appuser` | Dataverse application user with *Basic User* and *VRL Ledger Service* |
| `endpoint` | Service endpoint (Service Bus queue, JSON) plus an asynchronous step on conversation close. The SAS key goes from Azure into an environment variable and is never printed. **Not part of the solution**, because it is environment-specific |
| `verify` | Ledger row counts and the deployed functions |

Any step can be run on its own, e.g. `Deploy.cmd -Steps code`. Every run writes a transcript to `deploy\logs\`.

## 4. After installing (about 10 minutes, admin center)

1. **Roles.** Give **VRL Ledger Reader** to the representatives and supervisors who use the pane or the dashboard.
   The Function's application user already has *VRL Ledger Service*.
2. **Agent pane.** In Copilot Service admin center:
   1. Go to **Workspaces → Application tab templates → New**: Name `Customer history (VRL)`, Application type
      **Web resource**, parameters `webresourceName` = `bpc_/vrl/agentpane.html` and
      `data` = `contactid={customerRecordId}&conv={LiveWorkItemId}`.
   2. Go to **Workspaces → Session templates**. The system templates can't be edited, so **create** one (Type *Generic*,
      anchor tab *Customer Summary*) and add **Customer history (VRL)** under *Additional tabs*.
   3. Assign that session template to your messaging workstreams (**Workstream → Advanced settings → Sessions**).

   The pane waits for the conversation's customer to be linked and then shows the customer's unresolved issues and
   recent contacts, with the evidence behind each verdict.
3. **Dashboard.** Open `https://<your-org>.crm.dynamics.com/WebResources/bpc_/vrl/dashboard.html`, or add it to your
   supervisor app's site map as a web resource page.
4. **Identity for AI-agent conversations.** Anonymous chats can't be verified and are reported as *unverifiable*.
   Use authenticated chat, or a pre-chat **Email** question that Contact Center matches to a contact.

## 5. Verify

| Check | How |
|---|---|
| Cloud path | `Smoke-Test.cmd` posts two same-issue contacts through HTTP → Service Bus → Function → Dataverse and expects a false containment. It rotates the function key afterwards |
| Live path | Close a chat linked to a contact. About 2–3 minutes later, `Show-Ledger.cmd` lists it as `Pending` |
| Mapping | `Inspect-Conversation.cmd <conversation id>` (read-only) shows handling mode, bot outcome, identity and text |
| Scores | `Explain-Customer.cmd <contact id>` (read-only) shows every same-issue score and the signals behind it |
| Health | `Diagnose.cmd` shows the Dataverse service-endpoint jobs, the queue and dead-letter counts, and Function errors |

## 6. Upgrade

Put the new `_managed.zip` in `artifacts\solution\` and run `Deploy.cmd -Steps solution,code`. The upgrade keeps all
data, and components removed from the new version are deleted from the environment. After an adapter or scoring
change, run `Reingest.cmd` to re-map and re-score existing conversations. It is idempotent.

## 7. Uninstall

1. Remove the **service endpoint and step** (`VRL Dataverse Events`) with the Plugin Registration Tool. They are not in
   the solution, so deleting the solution does not remove them.
2. Delete the **VerifiedResolutionLedger** solution. **This deletes the ledger tables and their data.**
3. Delete the Azure resources named after `baseName` (`func-`, `plan-`, `sb-`, `kv-`, `st…`, `appi-`, `log-`, `id-`), the
   `text-embedding-3-small` deployment if nothing else uses it, and the app registration `vrl-ledger-dataverse`.
   Key Vault is soft-deleted; purge it if you redeploy with the same name.

## 8. Source mode (development)

Set `"install": "source"`, then run `Deploy.cmd`. It provisions the tables and keys from `SchemaDefinition.cs` and the roles from
`SchemaProvisioner.Roles`,
uploads the web resources and seeds synthetic data (`synthetic.customers`). Large Contact Center orgs can time out
creating tables through the metadata API. In that case, provision in a smaller environment and run
`Deploy.cmd -Steps promote -SourceDataverseUrl <that environment>` to import the solution asynchronously.

## 9. Releasing (maintainers)

Run `Package.cmd <version>` (e.g. `Package.cmd 1.1.0.0`) against the reference development environment. It:

1. lists every component outside the package allow-list (tables and columns `bpc_*`, the choice, keys, relationships,
   `VRL` roles, views, forms, charts, `bpc_/vrl/` web resources) and asks before removing them **from the solution**
   (they stay in the environment);
2. stamps the version, publishes, and exports `_managed.zip` and the unmanaged zip into `artifacts\solution\`;
3. opens the managed zip and **fails** if it contains a service endpoint, a plug-in step, a bot, Service Bus details or
   a definition of a Microsoft table.

Attach both zips to the GitHub release. `Solution-Report.cmd` (read-only) lists the solution's components at any time.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `prvCreateOptionSet` denied | The Default environment gives makers no System Administrator | Use an environment where you are System Administrator |
| "Failed to connect to Dataverse" | The active Azure CLI account belongs to the other tenant | `Deploy.cmd -Steps signin`; every call pins the credential to the right tenant |
| Device-code sign-in blocked (AADSTS530035) | Security defaults | Run on a workstation with a browser sign-in |
| SQL timeout creating tables | Large Contact Center org | Use the managed package, or the `promote` step |
| Chats never reach a representative | The trial's demo workstream routes to a read-only demo bot | Create your own workstream and queue, and give representatives a chat capacity profile |
| All AI-agent chats are *unverifiable* | Anonymous chat | Authenticated chat, or a pre-chat Email question |
