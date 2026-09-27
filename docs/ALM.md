# ALM

## Environments and sources of truth

| Artefact | Source of truth | How it reaches an environment |
|---|---|---|
| Tables, columns, keys, choices, roles | `SchemaDefinition.cs` + `SchemaProvisioner` (code-first, idempotent) | **Development**: `Deploy.cmd` in *source* mode (`vrl provision`) |
| Web resources | `webresources/src` → `build.mjs` → `dist/` | **Development**: `vrl deploy-webresources` |
| All Dataverse components above | The **managed solution** built from the reference development environment | **Test, acceptance, production**: `Deploy.cmd` in *managed* mode (`vrl import-solution`) |
| Azure resources | `infra/main.bicep` | `Deploy.cmd -Steps infra` in every environment |
| Function code | `src/` | `Deploy.cmd -Steps code` |
| Service endpoint + step | `vrl register-endpoint` (environment-specific, **never** in the solution) | `Deploy.cmd -Steps endpoint` |
| Configuration rows (windows, rates) | `vrl seed-config` defaults, then owned by the business | Data, not solution components; edited in the environment |

The solution's unpacked XML isn't source-controlled: the schema *is* code. The managed zip is a build output, published
as a GitHub release asset.

## Release

1. Merge to `main`. CI builds, tests (53 tests), builds the web resources and scans for secrets.
2. In the reference development environment: `Deploy.cmd -Steps dataverse,webres` so the environment matches the code.
3. `Package.cmd <major.minor.build.revision>`:
   - curates the solution to the allow-list, removing (not deleting) anything else;
   - stamps the version and exports managed and unmanaged zips;
   - **fails** if the managed zip contains a service endpoint, a plug-in step, a bot, Service Bus details or a Microsoft
     table definition.
4. Create a GitHub release with the tag `v<version>` and attach both zips. Record changes in `CHANGELOG.md`.

Set the development environment's **preferred solution** to *Default Solution*. Otherwise Copilot Studio and the admin
center add their components to the ledger solution. `Package.cmd` catches that, but it's cleaner to avoid it.

## Deploy to a downstream environment

`Deploy.cmd` with `"install": "managed"`:
- **Fresh install:** asynchronous import.
- **Upgrade:** holding solution + DeleteAndPromote. Data is kept, and components removed in the new version are deleted.
- **Same or newer version installed:** skipped.
- **Unmanaged ledger solution present:** refused.
- **Managed environments are never customised.** Provisioning and web-resource upload detect the managed solution and
  skip.

After a Function or scoring change that affects existing verdicts, run `Reingest.cmd` (idempotent).

## Versioning

- **Solution version:** `major.minor.build.revision`. Raise *major* for breaking schema changes; never renumber choice
  values.
- **Engine version:** `ResolutionEngine.Version`, stored on every verdict (`bpc_engineversion`), so any KPI can be traced
  to the scoring rules that produced it.
- The **embedding model id** is stored with every vector, and vectors from different models are never compared. After
  changing the model, run `Reingest.cmd` to re-embed.
