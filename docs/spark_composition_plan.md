# Plan — One composition system: library defaults, application overrides

PRD: [spark_composition_PRD.md](spark_composition_PRD.md). This is the same PR as
[generic_passkeys_page_plan.md](generic_passkeys_page_plan.md), which builds on it. Test suites
run only in the final sweep.

## Status

| Milestone | State |
|---|---|
| Grill Q1–Q6 | ⏳ |
| M0 Spikes S1–S5, S7 (S6 not run, Q1 = C) | ✅ 2026-10-06, PRD §9 |
| M1 DRY imports + one App_Data location (D11, D12) | ✅ 2026-10-06 |
| M2 Layering engine + kind specs (D2, D3) | ✅ 2026-10-06 (actions + model specs; translations in M5) |
| M3 Generalised library-layer generator (D1) | ✅ 2026-10-06 (PRD D1, D16 "As built in M3") |
| M4 Composed-model provider; synchronizer writes the delta (D5, D6) | ✅ 2026-10-06 (PRD D5, D6 "As built in M4"; SparkUser moved here from M9) |
| M5 Translations at runtime (D10) | ✅ 2026-10-06 (PRD D10 "As built in M5") |
| M6 Library rights (D4), if Q2 adopts it | ✅ 2026-10-06 (PRD D4 "As built in M6"; Moderation joins the engine, Q6) |
| M7 Gates with provenance + uniform reload + describe (D7–D9) | ✅ 2026-10-06 (PRD D9 "As built in M7": D7–D9) |
| M8 IManager rights at construction (D13) | ✅ 2026-10-06 (PRD D13a "As built in M8") |
| M9 Migrate libraries and apps | ✅ 2026-10-06 (PRD D4 "As built in M9"; M8's known gaps closed, PRD D13a; Passkeys moved to the passkeys plan) |
| M10 Docs and versions | ✅ 2026-10-07 (schema descriptions from `///` summaries on hover; composition docs, AGENTS.md, minor bumps to 11.0.0-preview.101 / ng-spark 22.30.0 / ng-spark-auth 22.20.0) |
| M11 Sweep | ⏳ |

## M0 — Spikes
Record the results in PRD §9, "Spike results", and amend D1–D13 where they disagree.

## M1 — DRY imports, one App_Data location
- `Directory.Build.props`/`.targets` import `spark.props`/`.targets` and the library targets once.
  Delete every hand-written `<Import>`. Add guards: Exe and not a test project, plus an opt-out.
- Add one App_Data resolver honouring `SparkAppDataDir`, and replace the ~24 runtime and
  generator sites.

## M2 — Layering engine
- `SparkLayers.Compose` + `KindSpec` (key rules, per-kind flags, provenance, conflict
  diagnostics).
- Port actions onto it, with a golden test against today's output (S2).

## M3 — Library-layer generator
- One generator embeds a library's `App_Data/**` (per kind) as assembly attributes. It replaces
  `LibraryActionsGenerator` and `LibraryTranslationsGenerator`.
- Discovery at runtime goes through the assemblies the application recorded
  (`SparkLayerAssemblies`, S1), `SparkAware` only as the fallback; at compile time through
  referenced-assembly symbols.
- A test runs the same app over ProjectReference and over a local-feed PackageReference (S1):
  `npm run test:layer-transport`, run in M11.

## M4 — Composed model
- `IModelSource` replaces every `Model/*.json` glob, including the
  `PersistentObjectNamesGenerator` input.
- UUIDv5 ids are written into shipped files and verified by the generator.
- The synchronizer, `TranslationsSeeder` and `SparkSchemaReference` write only the app delta.

## M5 — Translations at runtime
- Remove the host aggregator registry. The app layer is read and reloaded from disk.
- Generators read the library attributes plus the app's AdditionalFiles.

## M6 — Library rights (per Q2)
- `rights` becomes a keyed set; group tokens; `bindings`; the per-library opt-out `"libraries": { "<alias>": false }`
  (decision log row 7; its rights stay in the posture table as inert).
- Guard rails (grant-only, own resources only) enforced at startup and by the analyzer.
- `moderation.json` joins the engine (Q6): Moderation ships its defaults, privileges confer slots bound in
  `security.json`, the composed result stays an `IConfiguration` source below appsettings.

## M7 — Gates, reload, visibility
- Composed hashes with layer provenance for the model, actions, program units and rights (the full
  composed table).
- One watcher policy for all app layers.
- `--spark-describe <kind>`.

## M8 — IManager applies rights at construction
- Construction paths present per caller. Endpoints stop presenting twice. Add the named elevated
  accessor (per Q5).

## M9 — Migrations
- ✅ Core ships New/Edit/Delete (already) through the new generator (`libs/spark/MintPlayer.Spark/App_Data/actions.json`, alias `spark`).
- ~~Authorization ships `Passkeys`, `PasskeyRow` and their rights~~ **Not in this milestone:** the passkeys plan
  (`docs/generic_passkeys_page_plan.md`) builds them, as library layers, next. `SparkUser` already ships since M4.
- ✅ Moderation's printed `init` rights become library rights where the M6 guard rails allow: the `Moderation`
  pseudo-type's (`Review` to `moderation:reviewers`; `Review`, `Suspend`, `Audit` to the new slot
  `moderation:moderators`). Per-type grants (`Vote/Question`) stay printed for the app.
- ✅ M8's known gaps: the object-less `RefreshAttribute`, a hidden required attribute on a create, the
  `parentReference` leak test.
- Apps delete their duplicated grants. (Done in M4: CodeCoverage's `SparkUser.json` deleted, QnA's reduced
  to a `showedOn` delta, the E2E test pins the derived id.)

## M10 — Docs and versions
- Rewrite the composition docs and the generated `AGENTS.md`. JSON schemas cover the new keys
  (`libraries`, `bindings`, keyed `rights`).
- Minor bumps only (`11.x` NuGet, `22.x` npm).

## M11 — Sweep
- `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`,
  with its log written raw to a file.
- Also run a package-reference consumption check (S1's test) and `playwright_node` checks of the
  passkeys page.
