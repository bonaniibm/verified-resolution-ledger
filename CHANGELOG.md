# Changelog

## 1.0.1

- **Fix: stored episodes could be truncated.** When a customer was re-evaluated after an open contact's final
  predecessors had fallen outside the history horizon (a late maturation run, a backlog beyond the per-run cap, or a
  replay), the contact was evaluated alone: it lost its predecessor link and the stored episode kept only that contact.
  A three-contact journey could then count as a first-contact resolution. On the synthetic benchmark 49 of 972 stored
  episodes were affected; FCR from stored episodes read 91.7% instead of 85.3%, and cost per resolved outcome EUR 5.47
  instead of 5.83. Verdicts, containment, repeat rate and precision/recall were not affected, and the dashboard (which
  rebuilds episodes from interactions) already showed the correct values. The processor now always evaluates complete
  episodes (`ILedgerRepository.LoadEpisodeMembersAsync`). After upgrading, run `Reingest.cmd` to repair stored episodes.
- Dashboard: the first-contact resolution caption now states the rate's own numerator ("623 of 730 issues resolved on
  the first contact") instead of all resolved episodes.
- Tooling: publish, import and export wait and retry while Dataverse is busy with another solution operation; C# pinned
  to 12 so every SDK compiles the same semantics; gitleaks allow-list for Azure built-in role IDs.

## 1.0.0 (first public release)

- Engine: explainable same-issue scoring (case, intent, structured and bare references, semantic similarity of the
  customer's own words), episode stitching, maturing verdicts (Pending → VerifiedResolved / FalseContainment /
  FailedHumanResolution / PlannedFollowUp / Unknown), cost model, KPIs, synthetic generator with ground truth.
- Contact Center adapter: participant-based bot/human classification, Omnichannel transcript text and customer's words,
  self-declared e-mail identity (Medium), Copilot Studio topics through late enrichment.
- Azure: Flex Consumption Functions, Service Bus sessions per customer, managed identity, Key Vault, App Insights (Bicep).
- Dataverse: managed solution (4 tables, 1 choice, 2 roles, 2 web resources); code-first provisioning for development.
- Agent pane (Copilot Service workspace) and supervisor dashboard.
- Tooling: config-driven deployment (managed and source modes), packaging with content verification, reingest, explain,
  diagnostics.
- Privacy and security: identity fields hashed, text redacted (e-mail, cards, the linked contact's phones) before it is
  queued or stored; client-supplied customer keys and embeddings ignored on the HTTP API.
- Validated live on a Contact Center trial ([docs/VALIDATION.md](docs/VALIDATION.md)).
