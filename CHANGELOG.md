# Changelog

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
