# Calibration

The same-issue weights are **engineering defaults**. They're checked against a synthetic benchmark and a handful of
live chats, but they're not measured on your contact centre. Calibrate before you publish KPIs.

## The score

`SameIssueScorer` adds the signals below and compares the sum to `SameIssueThreshold` (default **0.60**). Scores are
clamped to 0–1.

| Signal | Default | Notes |
|---|---|---|
| `SameCase` | 0.70 | Both contacts linked to the same case. Enough on its own |
| `IntentExact` | 0.60 | Same intent code. Enough on its own |
| `IntentFamily` | 0.30 | Same family: an explicit family, or the intent's prefix before the first `.`, `/` or `:` (`billing.refund` → `billing`) |
| `IntentConflictPenalty` | −0.30 | Both have intents from different families |
| `SharedStructuredReference` | 0.50 | e.g. `ORD-4455667`, `TRK-55667788`, `CAS-01234-X1Y2`, a VIN. **Not** enough alone |
| `SharedReference` | 0.35 | A bare 7–12 digit number. The linked contact's own phone numbers are redacted to `[PHONE]` before scoring, so they can't count. Other phone numbers in the text (e.g. an anonymous customer's) can |
| `Semantic` | up to 0.65 | `0.65 × (cosine − floor) / (1 − floor)` above the model's floor |

Semantic floors per embedding model (`SimilarityWeights.ForEmbeddingModel`): `text-embedding-3-*` **0.50**,
`ada-002` 0.80, local hashing 0.30. Vectors are only compared when both come from the same model.

What you get with the defaults:
- A reference alone (0.50) doesn't link. A reference plus moderate similarity (a cosine above about 0.58) does.
- Text alone needs a cosine of about 0.96, so it effectively never links without corroboration.
- Live values: same issue 0.79–0.83, different issues of the same customer at most 0.17.

The embedding input is the **customer's own words** when the transcript identifies senders. Otherwise it's the full
conversation text or the Copilot summary.

## Procedure

1. **Sample.** Take about 200 pairs of consecutive contacts from the same customer within 7 days, stratified by channel,
   including AI-agent conversations. `Explain-Customer.cmd <contact id>` prints each pair's score and signals.
2. **Label.** Two people independently mark each pair as same issue or not. Resolve disagreements, and record the
   agreement rate: it caps the precision you can expect.
3. **Measure.** Put each pair's score (from step 1) next to its label, and compute precision and recall at the current
   threshold, e.g. in a spreadsheet. (`AccuracyReport` in `Vrl.Core` does this automatically, but only for the synthetic
   generator's labelled data.)
4. **Tune, in this order:**
   1. `SameIssueThreshold`, stored in the global *Repeat Window Policy* row, so no redeploy is needed.
   2. The semantic floor for your embedding model (`SimilarityWeights.ForEmbeddingModel`: a code change and redeploy).
   3. The reference weights, if your references are noisy (e.g. shared account numbers): also a code change.
5. **Hold precision at 90% or above.** A wrong "came back" verdict on a representative's contact costs more trust than a
   missed repeat. Accept lower recall, and report it.
6. **Re-run after changes.** `Reingest.cmd` re-maps and re-scores existing conversations, and stored explanations are
   refreshed.

Add tenant-specific reference patterns (policy numbers, VIN variants) through the `ReferenceExtractor` constructor.
Keep them strict: a loose pattern that matches unrelated numbers inflates false links.

## Windows

Default 72 hours; cases 7 days (`Repeat Window Policy` rows by channel or intent family). Choose the window per issue
type: a delivery question may legitimately recur after 5 days, a password reset shouldn't recur within 24 hours.
Longer windows raise measured repeat rates and delay verdicts.
