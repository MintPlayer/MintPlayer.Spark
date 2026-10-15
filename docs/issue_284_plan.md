# Plan — Issue #284 (one pull request)

PRD: [issue_284_PRD.md](issue_284_PRD.md). Baseline: master `a401c9e7`.

## Status

Not started. Run the spikes (M0) first; S1 and S2 can change D10 and the renderer rule.

## Milestones

### M0 — Spikes S1–S5 (PRD §5)
- Record each result under "Spike results" in the PRD, with file:line evidence, and amend D10 and the
  S1 renderer rule if the results call for it.

### M1 — Model property and schema (R1, R7, D1, D2, D12)
- `SparkQuery.ColumnOrder : string[]?`, ignored when written as null, with a `<summary>` describing
  the `!` prefix.
- Correct the doc comment on `SparkQuery.Columns`.
- Add `items.pattern` in the `SparkSchemaGenerator` transform (per S3) and regenerate `schemas/`.
- Add `columnOrder?: string[]` to the TS `models/src/spark-query.ts`.
- Leave the structural hash alone (D7). Add a test asserting that a `columnOrder` change doesn't move
  `modelHashes.json`.

### M2 — Runtime: `BuildColumns` (R3, R4, D7, D8)
- Parse the list once per query into (name, hidden) entries; this can be cached on the singleton
  model.
- `BuildColumns`: take the surface (unchanged), drop hidden entries, put listed entries in list order,
  and append unlisted surface attributes by (`order`, `name`).
- Make sure every caller passes the query (`QueryExecutor.cs:385,428`,
  `LiveAppStreamSubscription.cs:108`; `SparkSelectionResolver` per S5).
- Ship values only for the resulting columns, plus whatever the S1 outcome adds.
- Tests in `QueryResultProjector*Tests`: order, hiding, an off-surface entry ignored, a rights-denied
  entry ignored, absence giving byte-identical output, and sort/filter on a hidden column still
  allowed (D7).

### M3 — Metadata rights stripping (R4)
- `AttributeRightsEnforcement.ForQueryMetadataAsync` removes rights-denied names from `ColumnOrder`
  in both forms.
- Test next to the existing `SortColumns` and `Columns` stripping tests.

### M4 — Synchronizer (R5, D4, D5, D6, D10)
- Add a hook after the indexName loop (`ModelSynchronizer.cs:~189`) for root queries, and the same
  on the satellite and embedded paths.
- Append and remove with console notes. Skip queries with a null list.
- Write library-layer deltas per S4.
- If S2 succeeded, append columns the projection doesn't back as hidden.
- Tests in `ModelSynchronizerTests`:
  - append (order and name tiebreak)
  - remove a dead name
  - keep an off-surface entry
  - keep a `!` marker
  - leave a null list alone
- Add a fixed-point case to `SynchronizeIdempotencyTests` and a library-delta case to
  `SynchronizeLibraryDeltaTests`.

### M5 — Verify-model (R6)
- Errors: a duplicate, the same name both plain and `!`, an empty name or bare `!`, drift.
- Warnings: an off-surface entry; a visible entry the projection doesn't back (D10); and, per S1, a
  hidden column a renderer depends on.
- Tests in `SparkModelVerifyChecksTests`.

### M6 — Client: reference picker (D9)
- `spark-reference-picker` draws the executed result's `columns`, and `visibleAttributes` goes.
- Update `spark-reference-picker.component.spec.ts`.
- Check that the grid spec "renders the columns the result declares" still passes unchanged.

### M7 — CodeCoverage acceptance (R8)
- In `Repository.json`, give `Account` `"Query, PersistentObject"`.
- Give `GetRepositories` the column order `["Name","Account",…]` and `Account_Repositories` one
  ending in `!Account`.
- Check every other query over `Repository` for the new `Account` column, and give it a `columnOrder`
  where it isn't wanted.
- Update `ModelColumnGuardTests` and the D6 note in `docs/code-coverage/adoption-findings.md`.
- This is a production app: its `modelHashes.json` must not change (D7).

### M8 — Demo, E2E, docs (R9, R7)
- In HR, `Company_People` hides `Company`.
- Add an E2E test for the `GetPeople` and company-page grid headers and the reference picker's column
  order.
- Add a guide section on per-query columns, with the `!` syntax, opt-in, what the synchronizer does,
  and that the list is presentation only.
- Add a note for the editor, D11 (rename also renames `columnOrder` entries), to `vscode-extension`'s
  PRD when that branch is resumed.

### M9 — Full verification and PR
- Run `--spark-synchronize-model` twice on every app; the second run writes nothing.
- Run `--spark-verify-model` on every app; it exits 0.
- One local sweep: `RAVENDB_LICENSE=… npm run test:affected`. Then open one PR referencing #284.
