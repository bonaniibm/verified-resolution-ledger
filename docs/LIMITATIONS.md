# Known limits

Read this before putting the KPIs in front of management. Every limit below either lowers recall (missed repeats,
which make results look better than they are) or needs a decision from you.

## Measurement

| Limit | Effect | What to do |
|---|---|---|
| **Vague follow-ups aren't linked.** "Any news on my parcel?" has no reference, and short customer texts are only moderately similar | Missed repeats: containment and FCR look better than they are | Capture intents or a Copilot summary. A future *open-issue* signal (same customer, open episode, last 24 h) would close most of this gap |
| **The weights are engineering defaults**, validated on synthetic data and a handful of live chats | Precision and recall in your tenant are unknown until measured | Label about 200 contact pairs and calibrate ([calibration](Calibration.md)) |
| **Text similarity needs corroboration by design** (a cosine of about 0.96 alone) | Repeats without a reference, case or intent depend on those signals being present | Enable intents in Contact Center. Encourage references (order, ticket, case) in AI-agent topics |
| **Identity drives everything.** Anonymous chats are always *Unknown* | AI-agent containment can't be verified for anonymous traffic | Authenticated chat (High confidence), or a pre-chat e-mail question that Contact Center matches to a contact |
| Self-declared e-mail is linked at **Medium** confidence | A customer typing someone else's address would be linked to that contact | Turn it off with the Function App setting `Vrl__Adapter__IdentifyFromSelfDeclaredEmail=false` where that risk isn't acceptable |
| **Contact Center's own deflection is the baseline** | The ledger audits Contact Center's claim. Copilot Studio's own outcome is logged, not stored | Know which "deflection" number your organisation reports before comparing |
| The first contact is judged by the second | A customer who gives up and never returns counts as resolved (humans) or *Unknown* (abandoned AI-agent sessions) | Pair with CSAT or complaint data. Silent churn is invisible to any repeat-based metric |

## Platform

| Limit | Detail |
|---|---|
| **Copilot Studio transcripts arrive ~30 minutes after the chat** | Bot topics appear on the dashboard up to about 45 minutes after a conversation (15-minute enrichment timer). Topics are looked up for 6 hours |
| Only **chat** validated live | Voice, e-mail, case and social channels are mapped by label but untested on live data |
| Transcript and Copilot Studio formats are **not formal contracts** | The parsers are tolerant and tested on real anonymised samples. A format change degrades to "no text / no topic", never to a wrong verdict |
| Copilot Studio **generative orchestration** untested | Topic traces may differ from classic orchestration's `IntentRecognition` |
| The agent pane needs an admin-center setup | Application tab template and session template, per workstream. Not in the solution because session templates are workstream-specific |
| The service endpoint is **outside the solution** | Registered by `Deploy.cmd -Steps endpoint`, removed manually on uninstall |
| Client-side dashboard | Aggregates up to 50,000 rows in the browser. Above that, move reporting to Fabric (change tracking is enabled on the tables). No Power BI model is included |
| Phone numbers in free text | Only the linked contact's own numbers are redacted. Other numbers, names and addresses a customer types stay in the stored text |
| Secret rotation | `-RotateSecret` adds a new client secret; the old one stays valid until you delete it |

## Scale and operations

- Not yet load-tested. The design processes customers in parallel (Service Bus sessions, `maxConcurrentSessions` 16 per
  instance) and writes each customer's changes in `ExecuteTransaction` batches of up to 200 requests. Dataverse
  service-protection limits are the expected ceiling. Test at your volume.
- An identity with more than 250 contacts in the look-back horizon (switchboards, shared mailboxes, test contacts) is
  treated as unreliable and not linked.
- Embeddings: one Azure OpenAI call per contact. A failure degrades to scoring without semantic similarity; the message
  is not dead-lettered.

## Privacy

E-mail addresses and phone numbers become the customer key through an **unsalted SHA-256** hash. That's
pseudonymisation, not anonymisation: phone numbers can be recovered by brute force. See [SECURITY.md](../SECURITY.md) for
the recommended keyed hash and other data-protection notes.
