# Contributing

Issues and pull requests are welcome. Please read [known limits](docs/LIMITATIONS.md) first: many of them are good
first contributions.

## Ground rules

- **Evidence over assumptions.** Microsoft's transcript and conversation formats aren't formal contracts. When you
  change an adapter or parser, add an **anonymised real sample** under `tests/Vrl.Dataverse.Tests/Fixtures` (synthetic
  ids, `example.com` addresses, no names) and a test for it.
- **Precision first.** A change that raises recall must not drop precision below 90% on `vrl simulate` (default seed).
  Put the before/after numbers in the pull request.
- **The engine stays pure.** No I/O in `Vrl.Core`. Adapters and repositories live in `Vrl.Dataverse` and `Vrl.AI`.
- **Never renumber choice values** or rename logical names; they're persisted and referenced by reports.
- **No environment values in the repo.** Tenant ids, URLs and resource names belong in `deploy/config.json`
  (git-ignored). CI scans for secrets.

## Build and test

```powershell
dotnet build VerifiedResolutionLedger.sln
dotnet test VerifiedResolutionLedger.sln
dotnet run --project tools/Vrl.Tools -- simulate
node webresources/build.mjs
```

Deploy to your own development environment with `"install": "source"` (see [docs/INSTALL.md](docs/INSTALL.md)).
