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
| M3 Generalised library-layer generator (D1) | ⏳ |
| M4 Composed-model provider; synchronizer writes the delta (D5, D6) | ⏳ |
| M5 Translations at runtime (D10) | ⏳ |
| M6 Library rights (D4), if Q2 adopts it | ⏳ |
| M7 Gates with provenance + uniform reload + describe (D7–D9) | ⏳ |
| M8 IManager rights at construction (D13) | ⏳ |
| M9 Migrate libraries and apps | ⏳ |
| M10 Docs and versions | ⏳ |
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
- Discovery at runtime goes through `SparkAware`; at compile time through referenced-assembly
  symbols.
- A test runs the same app over ProjectReference and over a local-feed PackageReference (S1).

## M4 — Composed model
- `IModelSource` replaces every `Model/*.json` glob, including the
  `PersistentObjectNamesGenerator` input.
- UUIDv5 ids are written into shipped files and verified by the generator.
- The synchronizer, `TranslationsSeeder` and `SparkSchemaReference` write only the app delta.

## M5 — Translations at runtime
- Remove the host aggregator registry. The app layer is read and reloaded from disk.
- Generators read the library attributes plus the app's AdditionalFiles.

## M6 — Library rights (per Q2)
- `rights` becomes a keyed set; group tokens; `bindings`; the `libraries` opt-in.
- Guard rails (grant-only, own resources only) enforced at startup and by the analyzer.

## M7 — Gates, reload, visibility
- Composed hashes with layer provenance for the model, actions, program units and rights (the full
  composed table).
- One watcher policy for all app layers.
- `--spark-describe <kind>`.

## M8 — IManager applies rights at construction
- Construction paths present per caller. Endpoints stop presenting twice. Add the named elevated
  accessor (per Q5).

## M9 — Migrations
- Core ships New/Edit/Delete (already) through the new generator.
- Authorization ships `SparkUser`, `Passkeys`, `PasskeyRow` and their rights.
- Moderation's printed `init` rights become library rights.
- Apps delete their hand copies: `SparkUser.json` in CodeCoverage and QnA, and the duplicated
  grants. QnA keeps a `showedOn` delta. Update the E2E test that pins the QnA `SparkUser` id.

## M10 — Docs and versions
- Rewrite the composition docs and the generated `AGENTS.md`. JSON schemas cover the new keys
  (`libraries`, `bindings`, keyed `rights`).
- Minor bumps only (`11.x` NuGet, `22.x` npm).

## M11 — Sweep
- `RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected`,
  with its log written raw to a file.
- Also run a package-reference consumption check (S1's test) and `playwright_node` checks of the
  passkeys page.
