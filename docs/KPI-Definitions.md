# KPI definitions

One set of definitions, implemented twice: in `Vrl.Core.Analytics.LedgerKpiCalculator` (the `vrl simulate` report and the
tests) and in the dashboard web resource (client-side). Keep them in sync; a Power BI or Fabric model of your own should
use the same rules. Where the two differ in detail, it's noted below.

## Building blocks

| Term | Definition |
|---|---|
| **Decided** contact | Verdict is not *Pending*, i.e. its repeat window has closed, or it has already been repeated, or it can never be linked |
| **Unidentifiable** contact | Decided with verdict code `NoReliableIdentity` (identity confidence below Medium). Can never be seen to repeat |
| **Verifiable** contact | Decided and not unidentifiable |
| **AI-agent conversation** | Handling mode *BotOnly* or *BotThenHuman* |
| **Repeat** | A contact whose successor (a later same-issue contact from the same customer within its window) exists and whose verdict is not *PlannedFollowUp* |
| **Episode** | A chain of same-issue contacts from one customer. Status: *Open* (last contact pending), *Resolved* (last contact verified), *Unresolved* (ended without verification after several contacts), *Unknown* |
| **Repeat window** | Per contact: the intent-family rule, else the channel rule, else the global rule. Defaults: 72 h, cases 7 days. Configured in *Repeat Window Policy* rows without redeploying |

## AI agent

| KPI | Formula |
|---|---|
| **Reported deflection** | decided *BotOnly* ÷ decided AI-agent conversations. Microsoft's definition, deflected ÷ (deflected + escalated): a conversation the customer abandons still counts as deflected |
| **Verified containment** | decided *BotOnly* with verdict *VerifiedResolved* ÷ decided AI-agent conversations (the same denominator) |
| **Containment gap** | Reported deflection − verified containment, in percentage points |
| Composition of the gap | Of decided AI-agent conversations: held up (verified) · came back (*FalseContainment*) · abandoned and never returned (`SilentAbandonment`) · unverifiable identity (`NoReliableIdentity`) · other (e.g. planned follow-up) · escalated |

Unidentifiable conversations stay in these denominators on purpose: the platform counts them as deflected, and the
ledger shows that it can't verify them.

## Outcomes

| KPI | Formula |
|---|---|
| **Repeat-contact rate** | repeats ÷ verifiable contacts |
| **Interaction-derived FCR** | episodes *Resolved* with exactly one contact ÷ episodes with a known outcome (*Resolved* or *Unresolved*) |
| **Cost per contact** | total cost ÷ contacts in view |
| **Cost per resolved outcome** | total cost of closed episodes (any status except *Open*) ÷ *Resolved* episodes. Shows "–" when none are resolved |
| **Spend on contacts that came back** | Sum of the cost of decided contacts that have a successor |
| **False containment by bot topic** | *FalseContainment* ÷ verifiable *BotOnly* contacts, per Copilot Studio topic |
| **Repeats after a representative, by queue** | Dashboard: *FailedHumanResolution* ÷ verifiable *HumanOnly* and *BotThenHuman* contacts, per queue. The calculator's `ByQueue` counts all repeats (any successor) of verifiable non-bot-only contacts |

*Pending* contacts are excluded from every rate until they mature. Cost per contact is the exception: it covers all contacts in view. The dashboard flags a **small sample** below 30
verifiable contacts, and hides breakdown groups with fewer than 3 contacts once the sample is large enough.

## Cost model

All rates live in *Cost Rate* rows (owned by Finance, no redeploy). Defaults are placeholders, not Microsoft list
prices.

| Component | Formula | Default |
|---|---|---|
| AI | AI credits × credit price | €0.01 per credit |
| Telephony | telephony minutes × channel rate (voice falls back to the telephony rate) | €0.02 per minute |
| Labor | handle minutes ÷ 60 × labor rate (a per-queue rate wins) | €35 per hour, loaded |

AI credits per conversation are **estimates** in the Omnichannel adapter: 10 per digital AI-agent conversation and 35 per
voice bot minute. Tune them from your Power Platform admin center consumption reports, through the Function App settings
`Vrl__Adapter__DigitalCreditsPerBotConversation` and `Vrl__Adapter__VoiceCreditsPerBotMinute`.

## Verdict codes

| Code | Verdict | When |
|---|---|---|
| `WindowOpen` | Pending | No repeat yet, window still open |
| `NoRepeatInWindow` | VerifiedResolved | Window closed without a same-issue return |
| `RepeatAfterBot` | FalseContainment | A *BotOnly* contact was followed by a same-issue contact |
| `RepeatAfterHuman` | FailedHumanResolution | A human-handled contact was followed by a same-issue contact |
| `PlannedFollowUp` | PlannedFollowUp | Followed by a same-issue contact, but a follow-up was scheduled |
| `SilentAbandonment` | Unknown | The customer abandoned the AI agent and didn't return |
| `NoReliableIdentity` | Unknown | Identity confidence below the linking minimum (Medium) |

Each verdict stores its evidence in `bpc_verdictreason` (JSON): the message, the window, and every signal that
contributed to the link, with its weight.
