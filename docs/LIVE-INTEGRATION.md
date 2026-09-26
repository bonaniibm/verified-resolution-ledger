# Live integration with Contact Center

How a closed Contact Center conversation becomes a ledger interaction. Installation is covered in
[INSTALL.md](INSTALL.md).

## Trigger and timing

1. A conversation (`msdyn_ocliveworkitem`) is updated with a new `statuscode`. An **asynchronous step** on the service
   endpoint posts the event (JSON) to the Service Bus queue `vrl-dataverse-events`.
2. The router **waits 90 seconds** (a scheduled message, not a sleep), so Contact Center can write the transcript, then
   maps the conversation if it's closed (`statuscode` 4). Other updates are ignored.
3. The canonical interaction goes to the session queue `vrl-interactions` (one session per customer), and the processor
   re-evaluates that customer's open history.
4. **Copilot Studio topics arrive later.** The agent's transcript is written when the bot session ends (~30 min).
   `EnrichBotTopics` runs every 15 minutes (at :07, :22, :37 and :52), re-maps AI-agent conversations from the last
   6 hours that don't have a topic yet, and re-ingests them. The upsert is idempotent.

A closed conversation reached the ledger in 2–3 minutes during validation. Its bot topic followed within about 45 minutes.

## Mapping

The adapter reads only columns that exist in the environment (it intersects with live metadata and logs anything
missing), and maps channels by **label**, not by option number.

| Ledger field | Source |
|---|---|
| Channel | `msdyn_channel` label: voice/phone/call → Voice, SMS, e-mail, chat, social apps, entity/record → Case |
| Handling mode | Session participants (`msdyn_sessionparticipant` → `msdyn_ocsession` → `systemuser`): a user with `msdyn_botapplicationid` is the AI agent, any other user is a representative. If that query isn't available: the active agent's bot flag, then the agent-session flags |
| Native bot outcome | Bot only → *Resolved* (Contact Center's deflection); abandoned → *Abandoned*; bot then human → *Escalated* |
| Customer | `msdyn_customer` (contact or account); otherwise an e-mail from the pre-chat answers or **the customer's own** messages that matches exactly one active contact's `emailaddress1` (Medium confidence). If the transcript doesn't identify senders, only pre-chat answers are used |
| Case | `regardingobjectid` when it's a case |
| Text (`Summary`) | Copilot conversation summary if present, else the transcript: all messages in order, control and system messages removed, HTML stripped, capped at 2,000 characters; else the title. The linked contact's phone numbers are redacted |
| Adapter settings | Function App settings `Vrl__Adapter__IdentifyFromSelfDeclaredEmail` (default `true`), `Vrl__Adapter__DigitalCreditsPerBotConversation` (10), `Vrl__Adapter__VoiceCreditsPerBotMinute` (35). Put the same values in the `adapter` section of `deploy/config.json` so CLI replays (`Reingest.cmd`) map conversations identically |
| Customer's words (`IssueText`, not stored) | Transcript messages with `isFromAgent` = false (or tagged `FromCustomer`), excluding system notices. Used for similarity |
| Bot topic | Copilot Studio `conversationtranscript` whose start is within ±10 min **and** whose first event carrying `msdyn_liveworkitemid` (in practice `startConversation`) is this conversation. The last recognised topic (`IntentRecognition`, or `TopicStart`/`DialogRedirect` in older agents) that isn't a courtesy or system topic (*Greeting*, *Thank you*, *Escalate*, *Fallback*…) |
| Times and cost | `msdyn_initiatedon` / `msdyn_closedon`; handle time from `msdyn_conversationhandletimeinseconds`; bot minutes = duration − handle time; AI credits estimated (see [KPI definitions](KPI-Definitions.md)) |

The Omnichannel transcript is stored as a note on `msdyn_transcript`, linked by `msdyn_liveworkitemidid`: base64 JSON,
newest message first, with the message list nested as a JSON string. Neither transcript format is a formal Microsoft
contract. Both parsers are tolerant, are tested against real anonymised samples (`tests/Vrl.Dataverse.Tests/Fixtures`),
and fall back to "no text" or "no topic", never to a wrong verdict.

## Checking it in your tenant

| Question | Command (read-only) |
|---|---|
| How is this conversation mapped? | `Inspect-Conversation.cmd <conversation id>`: handling mode, participants, identity, text, topic |
| Why were two contacts linked, or not? | `Explain-Customer.cmd <contact id>`: every pair's score and signals |
| Is the pipeline flowing? | `Diagnose.cmd`: service-endpoint system jobs, queue and dead-letter counts, Function errors |
| What do the Copilot Studio transcripts look like? | `vrl bot-transcripts --since-hours 24 --dump <folder>` |
| What does an Omnichannel transcript look like? | `vrl dump-transcript --id <conversation id> --out <file>` |

After an upgrade that changes mapping or scoring, `Reingest.cmd` replays every conversation through the current code.

## Other sources

`POST /api/interactions` (function key) accepts the canonical interaction JSON from any system: embedded Salesforce or
ServiceNow, IVR vendors, load tests. `sourceSystem` + `sourceRecordId` make it idempotent. Client-supplied ledger state, pre-resolved customer keys
(`knownKey`) and embeddings are ignored: identity is always derived from the raw contact, e-mail, phone or external id. Put API Management with Entra ID in front of it for production.
