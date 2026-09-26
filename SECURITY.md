# Security and data protection

## Reporting a vulnerability

Please **don't open a public issue** for security problems. Use GitHub's *Report a vulnerability* (private security
advisory) on this repository. You'll get an acknowledgement within a few working days.

## What the ledger stores

| Data | Where | Protection |
|---|---|---|
| Conversation text (a Copilot summary, or the transcript capped at 2,000 characters; stored up to 4,000) | `bpc_interaction.bpc_summary` | Before storage, e-mail addresses become `[EMAIL]`, card-like digit runs `[CARD]`, and the **linked contact's own** phone numbers (mobile, business, home) `[PHONE]`. Other phone numbers, names and addresses in free text are **not** removed |
| Customer key | `bpc_customerkey` | `contact:<id>` (the contact's GUID, in clear) for contacts. E-mail and phone become `email:` / `phone:` + SHA-256 truncated to 128 bits (see below) |
| Embedding vector | `bpc_embedding` (512 floats) | Derived from the customer's words. Not reversible to text in practice, but treat it as personal data |
| Verdicts, scores, evidence, costs | `bpc_interaction`, `bpc_contactepisode` | No free text beyond the evidence (e.g. "Shared structured reference: TRK55667788") |

The ledger never stores the **identity fields** (raw e-mail, raw phone), and the Functions never log them: they're hashed into
the customer key. The CLI's diagnostic output masks them. Messages
are normalised *before* they're queued, so Service Bus (7-day TTL, dead-letter) holds the same redacted text and hashed key
as Dataverse. A self-declared e-mail is used only to look up a contact.

**Hashing is pseudonymisation, not anonymisation.** The hash is unsalted, so phone numbers (low entropy) can be recovered
by brute force from `bpc_customerkey`. For production, replace it with a keyed hash (HMAC-SHA-256 with a secret from
Key Vault). This is on the roadmap. Customers linked to a contact don't use the hash at all.

**Deletion.** Lookups to contact, account and case use *remove link*: deleting a contact (e.g. a GDPR erasure) removes the
lookup but **keeps the ledger rows**, which still hold the contact id in `bpc_customerkey` and free text in `bpc_summary`.
For erasure, delete the customer's `bpc_interaction` and `bpc_contactepisode` rows by `bpc_customerkey`.

**Processors.** The customer's words are sent to **your** Azure OpenAI deployment for embedding (no training on your data,
in your tenant and region). Nothing leaves your Microsoft tenants.

## Identities and secrets

| Hop | Identity | Secret |
|---|---|---|
| Function → Storage, Service Bus, Key Vault, Azure OpenAI, App Insights | User-assigned managed identity with data-plane RBAC roles only | None. Storage has shared keys disabled |
| Function → Dataverse (can be another Entra tenant) | App registration `vrl-ledger-dataverse` → Dataverse application user with *VRL Ledger Service* | Client secret (1-year expiry). `Deploy -Steps secret` creates it, writes it to a local temporary file that is deleted immediately after `az keyvault secret set`, and never prints it. The Function reads it through a Key Vault reference. `-RotateSecret` **adds** a new secret; remove old ones with `az ad app credential delete` |
| Dataverse → Service Bus | Service endpoint | **Send-only** SAS rule on one queue. The key moves from Azure CLI to an environment variable for registration, never onto a command line or into a log. SAS authentication therefore stays enabled on the namespace, so its default *RootManageSharedAccessKey* also exists: nothing uses it, and you can restrict it by policy |
| HTTP ingestion API | Function key | Front with API Management + Entra ID for production. The smoke test rotates the key after use. Client-supplied customer keys, ledger state and embeddings are ignored |

When Dataverse and Azure are in the **same** tenant, the client secret can be replaced by a managed identity in
Dataverse (federated credential). That's on the roadmap.

## Dataverse roles (as shipped in the managed solution)

**VRL Ledger Service** (the Function's application user):
- Create, read, write, delete, append and append-to on the ledger data tables `bpc_interaction` and `bpc_contactepisode`.
- Read only on the configuration tables `bpc_windowpolicy` and `bpc_costrate` (owned by the business).
- Read only on the Contact Center sources the adapter needs: contact, account, case, activities (conversations,
  sessions), notes (transcripts), queue, user, transcripts, session participants, pre-chat answers and Copilot Studio
  conversation transcripts.
- Append-to on contact, account and case, which is required to set the ledger's lookups.
- **No write, delete, assign or share on any Microsoft table.**

**VRL Ledger Reader** (representatives and supervisors using the pane and the dashboard): read on the ledger tables only.

Dataverse adds a few default privileges to every new role (for example SharePoint document create and write, and
plug-in metadata read). Those come from the platform, not from this solution.

## Operational

- Every deployment run writes a transcript to `deploy/logs/` (git-ignored). Secrets are never written to it.
- `deploy/config*.json` holds tenant ids and resource names. It's git-ignored; never commit it.
- CI scans every push to `main` and every pull request for secrets (gitleaks).
- Representative-level results are aggregated by queue by design. Don't use this ledger for individual performance
  management without involving your works council and DPO.
