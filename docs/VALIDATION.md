# Validation record

Version 1.0 was validated in two ways: on a **live Dynamics 365 Contact Center trial**, with real chats, a real Copilot Studio
agent, and the deployed Azure pipeline; and on a **labelled synthetic benchmark**. This page records what was tested,
the evidence, and the defects the live test exposed.

## Live environment

| Component | Configuration |
|---|---|
| Dataverse | Dynamics 365 Contact Center trial (Copilot Service workspace), live chat |
| AI agent | Copilot Studio agent (classic orchestration, one topic *Parcel not received*, no escalation), connected to Contact Center |
| Workstreams | One for representatives (no AI agent), one with the AI agent; pre-chat e-mail question on the AI-agent widget |
| Azure | Flex Consumption Functions (.NET 8 isolated), Service Bus Standard (sessions, duplicate detection), Key Vault, App Insights |
| Embeddings | Azure OpenAI `text-embedding-3-small`, 512 dimensions |
| Tenancy | Dataverse and Azure in **different Entra tenants** |

Pipeline: conversation closed → Dataverse service endpoint → Service Bus → router (waits 90 s for the transcript) →
Omnichannel adapter → per-customer session queue → processor → Dataverse ledger. A closed conversation reached the
ledger in about **2–3 minutes**.

## Scenarios

All scenarios use one test contact. The scores are the final values after the fixes listed below.

| # | Scenario | Result | Evidence |
|---|---|---|---|
| A→B | Refund chat handled by a representative; the customer returns about an hour later about the same refund | A = **FailedHumanResolution** (`RepeatAfterHuman`), final | Shared `ORD-4455667` (+0.50), text similarity 0.754 (+0.33): **0.83** |
| C | The AI agent answers "my parcel shows delivered but I never received it"; the customer says "OK thanks" and closes. Seven minutes later the customer asks a representative about the same parcel | The bot chat = **FalseContainment** (`RepeatAfterBot`), final. Topic *Parcel not received* | Shared `TRK-55667788` (+0.50), text similarity 0.721 (+0.29): **0.79** |
| — | Parcel contacts compared with refund contacts | Separate episodes | Highest cross-issue score **0.17** (text only, no shared reference) |
| — | Seven anonymous chats stuck with the trial's read-only demo bot, force-closed | **Unknown** (`NoReliableIdentity`), final. Excluded from repeat and FCR rates | Contact Center counts all seven as bot-only, i.e. *deflected* |
| — | A vague follow-up, "Hi, any news on my parcel?" (no reference) | **Not** linked (score 0.06) | Recall gap for vague follow-ups, see [limitations](LIMITATIONS.md) |

**Three views of one conversation (scenario C):**

| Source | Verdict |
|---|---|
| Contact Center (bot-only conversation) | Deflected |
| Copilot Studio analytics (`SessionInfo`) | Abandoned, reason `UserExit`, no implied success |
| Verified Resolution Ledger | **False containment**: the customer returned about the same parcel 7 minutes later |

**Surfaces verified live**:
- **Agent pane in the Copilot Service workspace.** On accepting the next chat from the test contact, the pane waited
  until the customer was linked. It then showed "Contact #5 in 2 h", both unresolved issues (the parcel issue flagged
  "AI agent said resolved, customer came back"), and the evidence behind each verdict.
- **Supervisor dashboard.** Reported deflection 100% (8 of 8 decided AI-agent conversations) against verified containment
  0%. Root cause: *Parcel not received*, 1 of 1 false containment.
- **Cloud smoke test** (HTTP → Service Bus → Function → Key Vault → Dataverse across tenants + Azure OpenAI): passed on the
  development instance, which has the same Azure topology.
- **Managed solution package.** Exported and checked: only the 4 ledger tables, 1 choice, 2 roles and 2 web resources.
  Its only external dependency is the Case table (Customer Service).

## Defects found by the live test

None of these showed up in the synthetic runs. Each was fixed and retested live.

| # | Symptom | Cause | Fix |
|---|---|---|---|
| 1 | Similarity compared conversation titles | The adapter used the conversation title when no Copilot summary existed | Read the Omnichannel transcript note (base64 JSON), extracted tolerantly |
| 2 | Bot-only chats looked "bot then human" | The bot's own user counted as the active *human* agent | Classify every session participant: a user with a bot application id is the AI agent |
| 3 | `ORD‑4455667` matched only as a bare number | Chat text carried U+2011 (non-breaking hyphen) | Fold all typographic dashes before extracting references |
| 4 | The bot chat scored 0.50 and was not linked | A shared structured reference weighed the same as a bare number | Structured references +0.50, bare numbers +0.35. Neither is enough alone |
| 5 | Explanations kept old scores after recalibration | Change detection ignored score and explanation | Persist and compare the score and the explanation JSON |
| 6 | Transcript text read newest-first | Omnichannel stores messages newest-first | Order by message timestamp |
| 7 | Repeat rate and FCR were flattered | Unidentifiable contacts counted in the denominators | Exclude them from repeat, FCR and root-cause rates, and show how many were excluded |
| 8 | Bot topics were never found | Real transcripts trace `IntentRecognition`, not `TopicStart`, and are written ~30 min after the chat | Parser rewritten against real transcripts. A 15-minute timer enriches late transcripts, re-published with a distinct message id so the 30-minute duplicate detection keeps it |
| 9 | Bot boilerplate diluted similarity (cosine 0.616) | "Hello, I'm … How can I help?" was embedded with the customer's words | Embed only the customer's messages (`isFromAgent` = false). Cosine rose to 0.721 |

How the parcel pair's score changed through the fixes: **0.50** (not linked) → **0.65** (defects 3–4) → **0.79** (defect 9).

## Synthetic benchmark

`vrl simulate --customers 600 --days 30` (seed 42) generates labelled journeys and replays them through the real
processor in close-event order, using local embeddings:

| Measure | Value |
|---|---|
| Interactions | 1,118 (93 still pending) |
| Native bot deflection | 80.2% |
| **Verified containment** | **51.5%**, against a true containment of 52.3% |
| Containment gap | 28.7 points |
| Repeat detection precision / recall | **93.7% / 76.1%** (42 in-scope repeats missed) |
| Out-of-scope repeats | 53 (anonymous, or outside the window) |

Recall is limited mainly by contacts with neither an intent nor a shared reference. The benchmark result did not
change with the live-test fixes, because synthetic data has no typographic dashes or bot boilerplate. Treat it as a
regression guard, not a claim about your tenant. Calibrate on your own data ([calibration](Calibration.md)).

## Not yet validated

- Production volume (thousands of conversations a day), and Dataverse service-protection limits under load.
- Voice, e-mail, case and social channels on live data. The adapter maps them, but only chat was tested live.
- A managed install into a clean environment, and an upgrade between two managed versions.
- Authenticated chat, and Copilot Studio generative orchestration (transcripts may trace topics differently).
