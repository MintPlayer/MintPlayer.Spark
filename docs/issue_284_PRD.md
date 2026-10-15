# PRD — Issue #284: Per-query grid columns (`columnOrder`)

Every query over an entity draws the same columns today: `QueryResultProjector.BuildColumns` keeps the
attributes with `ShowedOn.Query` and sorts them by `order`, and never looks at the query. This PRD gives
each query an optional **ordered string array of attribute names**, `columnOrder`, that reorders the
query's columns and can hide some of them. A missing array means today's behaviour exactly. The model
JSON is edited by hand or by the planned VS Code / Visual Studio extension, not by the running web app,
so columns are referenced **by attribute name**. There is no per-column GUID. `--spark-synchronize-model`
keeps an existing array complete by appending surface attributes it doesn't list yet, and removes names
of attributes that no longer exist.

- Issue: https://github.com/MintPlayer/MintPlayer.Spark/issues/284
- Implementation plan: [issue_284_plan.md](issue_284_plan.md) (decisions D1–D16 below)
- Baseline: master `a401c9e7`
- Prior art: the #279 out-of-scope note (`docs/issue_279_PRD.md:230-238`); CodeCoverage D6
  (`docs/code-coverage/adoption-findings.md:48`, `:229-235`); the Vidyano per-query `Columns` list (§6)

## 1. What already exists on master

The issue text is partly out of date. Since #327/#328 ("a row is a projection") the **server** decides
the columns; the client no longer works them out from the entity type.

| Concern | Where | Today |
|---|---|---|
| Column set + order | `libs/spark/MintPlayer.Spark/Services/QueryResultProjector.cs:51-80` `BuildColumns` | `ShowedOn.HasFlag(Query)`, `.OrderBy(a => a.Order)` |
| Callers of `BuildColumns` | `QueryExecutor.cs:385,428`; `LiveAppStreamSubscription.cs:108`; `SparkSelectionResolver.cs:108` (no query); share links filter `result.Columns` (`SparkShareLinks.cs:237`); MCP reads `result.Columns` (`SparkMcpResult.cs:54`) | All pass through the entity-wide surface |
| Value cells | `ToItemsAsync(..., columns, ...)` (`QueryExecutor.cs:390,434`) | Only the columns' values are shipped (#264 G-Q4: "no shipped-but-not-drawn value") |
| Main grid / query card / detail-page sub-queries | `ng-spark/grid/src/spark-query-grid.component.ts:695-701` | Draws `QueryResult.columns`, so it already follows the server |
| Reference picker | `ng-spark/po-form/src/spark-reference-picker.component.ts:101-106` | **Still works out its columns from the entity type** (`showedOn` Query + `order`) |
| AsDetail tables, row renderers | `pipes/src/as-detail-columns.pipe.ts:13-29`, `renderers/src/row-renderers.ts:30-32` | Nested types with no query, so not affected |
| `SparkQuery.Columns` | `Abstractions/SparkQuery.cs:64-84,169-182` | **Takes the JSON key `"columns"`**: a sparse capability override `{name, canSort?, canFilter?, canListDistincts?}` (#431/#440). Its doc comment says it is "never a column enumeration … that remains showedOn plus order". Used by Fleet `Car.json`. It is hashed order-insensitively (`ModelFileShape.cs:259-269`) and verified (`SparkDevelopmentExtensions.cs:772-829`) |
| Query metadata on the wire | `SparkCatalogue.cs:108-142` → `AttributeRightsEnforcement.ForQueryMetadataAsync` (`:288-312`) | Removes Query-denied names from `SortColumns`, `Columns` and `ParentReference` ("metadata must not say it exists") |
| Sort/filter/search/distinct gates | `ColumnCapabilities.FindQuerySurfaceAttribute` (`ColumnCapabilities.cs:107-121`); `QueryExecutor.cs:2664-2680, 2298-2307, 2137-2145, 153-156` | Keyed on `ShowedOn.Query` + attribute rights, the authorization boundary |
| Sub-queries | `EntityTypeDefinition.Queries : SparkSubQuery[]` (`SparkSubQuery.cs:23-50`) | Refer to a `SparkQuery` by alias, name or id, so a per-query setting already applies to sub-queries |
| Synchronizer | `ModelSynchronizer.SynchronizeModels` (`ModelSynchronizer.cs:59-389`) | Round-trips existing `SparkQuery` objects (`:233`); never creates sub-queries; "never deletes" (#253) |
| Ids | `SparkQuery.Id`, attribute `id` are GUIDs | Every other cross-reference uses **names** (security.json resources, translations, `query` on references, sub-query aliases). Program units are the one place that uses query GUIDs |
| Schemas | `tools/SchemaGenerator` → `schemas/model.schema.json`; CI mints `schemas/v{n+1}` on any diff (`dotnet-build-master.yml:31-111`) | Strict (`additionalProperties:false`) |
| Editor | Branch `vscode-extension` only (`docs/PRD-SparkEditor*.md` at `5d5b56bef`) | Saves by matching GUID ids; plans nothing for per-query columns yet |

## 2. Requirements

### R1 — A query can reorder and hide its columns
`SparkQuery` gains `string[]? ColumnOrder`, serialised as `"columnOrder"`. Each entry is an attribute
name. A name prefixed with `!` is **listed but hidden**. It keeps its place in the list, so un-hiding
puts it back where it was:

```json
{ "name": "Account_Repositories", "source": "Custom.Account_Repositories",
  "columnOrder": ["Name", "IsPrivate", "LatestCoverage", "FullName", "LatestCoverageSha", "!Account"] }
```

### R2 — Absent means unchanged
When `columnOrder` is null or absent, every column, value, capability and wire byte is the same as on
the baseline. The synchronizer never creates the array (D4), so no existing model file changes.

### R3 — One source of truth: `BuildColumns`
The query's `columnOrder` is applied inside `BuildColumns`, so every surface agrees: execute, paging,
live/app-stream, share links, MCP, selection resolution (where the query is known) and the reference
picker. The grid needs no change.

### R4 — It only narrows and reorders; it never widens
An entry can only pick from what the query would show without it: `ShowedOn.Query` and the user's
attribute rights. Naming a PO-only, `None` or rights-denied attribute does not surface it. Query
metadata removes rights-denied names from `columnOrder`, plain and `!`-prefixed, the same way it
already does for `SortColumns` and `Columns`.

### R5 — The synchronizer keeps an existing list complete and alive
For every query whose `columnOrder` is non-null:
- **Append** each query-surface attribute that the list mentions in neither form. Append them visible,
  at the end, ordered by (`order`, `name`), and print a note:
  `Appended column 'X' to columnOrder of query 'Q'.`
- **Remove** each entry whose name matches **no attribute** of the entity, and print a note:
  `Removed column 'X' from columnOrder of query 'Q': no attribute named 'X' exists.`
  This is a narrow, documented exception to #253 (D5).
- Change nothing else: keep the order, the `!` markers, and entries for existing attributes that are
  currently off the surface.
- Reach a fixed point: run 2 and run 3 are byte-identical (`model_sync_lifecycle_PRD.md` R12).

### R6 — Verify-model checks the list
`--spark-verify-model` treats the following as errors (exit 3):
- a duplicate name;
- a name listed both plain and `!`;
- an empty name or a bare `!`;
- an entry or an append the synchronizer would change (drift).

It **warns** about an entry for an existing attribute that is off the Query surface, because it is
ignored at runtime.

### R7 — Schema and docs
- Regenerate the schema; the new property's `<summary>` becomes its description. Each item has the
  pattern `^!?[A-Za-z_][A-Za-z0-9_]*$` (S3).
- Correct the `SparkQuery.Columns` doc comment: it is still not a column enumeration, but
  `columnOrder` now is.
- Add a guide section on per-query columns.

### R8 — Acceptance: CodeCoverage repositories grids
- `Repository.Account` gains `Query` in `showedOn`.
- `GetRepositories` lists Name first, then Account.
- `Account_Repositories` lists `!Account`, so the account page's first, linked column is Name again.
- `ModelColumnGuardTests` pins both shapes.

### R9 — Demo and E2E
In HR, `Company_People` hides the constant `Company` column while `GetPeople` shows it. An E2E test
asserts both grids' headers and the reference picker's column order.

## 3. Out of scope

- **Per-parent overrides on `SparkSubQuery`.** Two parents that want different columns author two
  queries, as CodeCoverage already does with `GetRepositories` and `Account_Repositories`.
- **Per-column width and label overrides** (Vidyano's `Width`). Nothing asks for them. If they are ever
  needed they belong in the existing `Columns` override objects, not in `columnOrder`.
- **Narrowing sort, filter or search by `columnOrder`** (D7).
- **Detail-page field order.** `order` keeps driving the PO page; `columnOrder` is grids only.
- **Changes to the editor extension.** Recorded here as a requirement on the editor (§7, D11), not built
  in this PR.

## 4. Open questions

1. ~~Same key as the existing `columns`?~~ **Resolved 2026-10-15:** no, use a new key (D1).
2. ~~How to hide when the synchronizer appends everything?~~ **Resolved 2026-10-15:** use a `!` prefix
   in the same array (D2).
3. Should a query bound to a non-default projection show the projection's fields by default rather than
   the entity's? Partly answered by D10; the rest depends on S2.

## 5. Spikes

- **S1 — Renderers that read sibling cells.** List every renderer that reads another column's value
  through `valueFor` (`query-result.ts`), for example the CodeCoverage sparkline. Hiding a column stops
  its value being shipped (R3). Decide whether such a renderer must declare its dependency, or whether
  hidden-but-depended-on columns still ship their value without a header. Lean: a verify-model error
  naming the renderer, to keep #264 G-Q4.
- **S2 — Can the synchronizer find a query's own projection?** It runs in the builder phase with no
  database and only knows the default projection (`ModelSynchronizer.cs:142-149`). Check whether
  `IIndexCatalog` can be resolved by reflection there for a query with `indexName`. If it can, D10
  appends the attributes that projection does not back as `!X`, hidden. If it can't, append them
  visible and let verify warn.
- **S3 — The schema pattern.** Confirm that `JsonSchemaExporter`, with a transform in
  `SparkSchemaGenerator`, emits `items.pattern` for `ColumnOrder`. This checks syntax only; a misspelled
  name is `--spark-verify-model`'s job (see the results).
- **S4 — Library layers.** A query defined in a library with `columnOrder` added in the app has to be
  written as an app delta (`WriteModelFile`, `ModelSynchronizer.cs:737-747`), and appending must not
  copy the whole library query into the app file.
- **S5 — Callers without a query.** `SparkSelectionResolver.cs:108` calls `BuildColumns` with no
  query. Confirm it only needs ids and capabilities, so `columnOrder` does not matter there, or pass
  the query through.

## 6. Vidyano reference (for parity decisions)

In Vidyano a persistent object owns the attribute pool, and each query owns `Columns[]` with
`Attribute` ($ref), `Offset`, `IsHidden` and `Width`. Two queries over the same PO show different
columns in a different order. `columnOrder` covers `Offset` (array position) and `IsHidden` (`!`).
It references attributes by name instead of by `$ref`/GUID, because Spark's other cross-references
already use names. It leaves out `Width`.

## 7. Decisions and spike results

| # | Decision | Evidence / reason |
|---|---|---|
| D1 | New key `columnOrder`, not `columns` | `"columns"` is an array of objects in the C# type, the schema (`model.schema.json:703`), the hash (`ModelFileShape.cs:259-269`), verify (`SparkDevelopmentExtensions.cs:772-829`) and rights stripping (`AttributeRightsEnforcement.cs:288-312`), and Fleet's `Car.json` uses it. Reusing it would mean a union converter and changing every reader. |
| D2 | One string array; `!Name` = listed but hidden | The owner asked for a string array per query. The synchronizer appends what is missing, so leaving a name out cannot mean "hidden"; it would be re-added. With one list a hidden column keeps its position, the synchronizer's "missing?" check is a single lookup, and an editor's drag-and-eye-toggle list maps onto it 1:1. A second `hiddenColumns` array was rejected because the two lists could disagree. |
| D3 | Attribute names, no column GUIDs | Owner preference: the model is edited by files and an extension, not by the running app. Security, translations, references and sub-queries already refer to things by name. |
| D4 | Opt-in: the synchronizer maintains the list, never creates it | Writing it into every query would churn every model file. This follows the same nullable-to-avoid-stamping reasoning as `Columns` (`SparkQuery.cs:78-83`). The editor creates the list on the first reorder or hide, from the current surface. |
| D5 | Remove an entry only when no attribute has that name; keep off-surface entries | #253 "never deletes" protects data. A `columnOrder` entry is a reference, like a dead `indexName`, which #279 already retargets with a note. Keeping off-surface entries means a column whose `showedOn` regains `Query` comes back in the same place. Attributes kept over from #253 still exist, so their entries stay. |
| D6 | Appended in (`order`, `name`) order, visible | Deterministic, so a fixed point is reached; attributes are written by name and reflection order is unstable (sync report §6). Appending visible matches what a query without a list shows. |
| D7 | Presentation only; sort, filter, search and distincts keep the `ShowedOn.Query` + rights boundary | A hidden column is not a security statement, since the same user sees it on the sibling query. It stays out of the structural hash, like `sortColumns` (`ModelFileShape.cs:229-232`). The client offers no UI on a hidden column. Authored `sortColumns` may name a hidden column. |
| D8 | Applied in `BuildColumns`; at runtime, surface attributes missing from the list go at the end, as in D6 | Runtime and synchronizer agree before the synchronizer has run. It is the single point all wire surfaces already pass through (R3). |
| D9 | The reference picker switches to the executed result's `columns` | It is the only client grid that still works out its own columns (`spark-reference-picker.component.ts:101-106`), and `executeQueryByName` already returns them. |
| D10 | Projection-bound queries: no automatic default to the projection's field set | Hiding stays explicit and editable. If S2 shows the synchronizer can resolve the projection, the columns it doesn't back are appended hidden. Verify warns about a visible entry the projection doesn't back, which today renders as a null-filled column (F279.10). |
| D11 | Requirement on the editor: renaming an attribute also renames its `columnOrder` entries (both forms) | Without it, a rename in the editor turns into remove-and-append at the next sync, and the column loses its place. |
| D12 | TS `SparkQuery` gets `columnOrder?: string[]` | The wire carries it (after rights stripping); typed for completeness. The grid does not read it. |

| D13 | A renderer's sibling dependency is checked, not shipped: `--spark-verify-model` **warns** when a `rendererOptions` string value whose key ends in `Attribute` names an attribute that the query's columns exclude (off the Query surface, or `!`-hidden) | S1. Shipping hidden values would bring back the ship-but-don't-draw exception that #264 G-Q4 removed. A warning rather than an error, because CodeCoverage's `Commit.Sha` `titleAttribute: "Message"` is already dead on the grid and still works on the detail page. |
| D14 | CodeCoverage renderers declare the siblings they read: short-sha gets `linkAttribute` (was the hard-coded `'FullName'`) and account-avatar gets `altAttribute` (was the hard-coded `'Login'`) | S1. Siblings named only in TypeScript are invisible to the D13 check. Commit rows never linked because `Commit` has no `FullName`, a bug that went unnoticed. The model JSON now states the dependency where the check can see it. |
| D15 | `SparkSelectionResolver` passes the query to `BuildColumns` | S5. Its rows become a custom action's `SelectedItems`, so the fallback branch has to shape `Values` the same way as the branch that re-runs the query. |
| D16 | The synchronizer and verify leave a library-owned `columnOrder` alone (library layer's list deep-equals the composed list) | S4. Appending or removing would copy the whole library list into the app file and freeze it there. The runtime append (D8) already fills the gap. |

### Spike results

**S1 — renderers that read sibling cells.** No ng-spark library grid renderer reads a sibling; every `valueFor` uses `column.name`.
- Only CodeCoverage's grid renderers read siblings:
  - short-sha: `rendererOptions.titleAttribute`, plus a hard-coded `'FullName'` (`short-sha-renderer.component.ts:39-47`)
  - account-avatar: hard-coded `'Login'` (`account-avatar-renderer.component.ts:45`)
- Two dependencies are already silently dead since #264. `Commit.Message` is PO-only (`Commit.json:143` against `:239`), and `Commit` has no `FullName`, so commit rows never link.
- The sparkline reads only the row id; the example in the original S1 text was wrong.
- The server ships `Id`, `Breadcrumb`, `Etag` and one value per column only (`QueryResultProjector.cs:174-180`).

Outcome: D13 and D14. A custom action reading a hidden column from `SelectedItems` gets an empty cell, the same on both resolver branches (D15).

**S2 — the synchronizer can resolve a query's own projection.** `--spark-synchronize-model` builds a frozen, reflection-only `IndexCatalog` before the synchronizer runs (`SparkDevelopmentExtensions.cs:1361-1367, 1399-1434`). Then:
- `indexCatalog.GetByIndexName(q.IndexName)?.ProjectionType` gives the projection's properties. A view-less index goes through `ViewlessQuerySurface` (`ModelSynchronizer.cs:1675`).
- For the **default** index nothing is new, because `showedOn` is already narrowed to the projection (`:927-941`).

Outcome: D10 is confirmed. Only for a query bound to a non-default index, the surface attributes that projection doesn't back are appended **hidden** (`!X`). Verify warns about a visible entry that the projection doesn't back.

**S3 — schema pattern.** `JsonSchemaExporter` ignores DataAnnotations. A property-keyed `Transform` case fits, alongside `ActionsFileEntry.confirmation` (`SparkSchemaGenerator.cs:137-145`), and `items.pattern` was emitted as intended.
- Checked with ajv: it accepts `"Name"`, `"!Name"` and `null`, and rejects `"!"`, `""`, `"!!Name"`, `"a.b"` and `"Name "`.
- **Correction:** the shared schema cannot flag a misspelled `"Nme"`, because valid names differ per file. Only `--spark-verify-model` catches unknown names (R6). The schema catches syntax only.
- All 671 attribute names across 87 model files match the pattern.
- `schemas/` is gitignored build output, so the PR carries the generator change, not schema files.

**S4 — library layers.** `queries` merge by `name`, and an array inside a query is replaced whole (`SparkLayers.cs:442-456`). `WriteModelFile`'s delta emits `{"name":Q,"columnOrder":[…]}` without copying the query (`SparkLayers.cs:295-353`), the same way an app already overrides `sortColumns` on a library query. No new mechanism is needed. Outcome: D16.

**S5 — `BuildColumns` without a query.** `SparkSelectionResolver.cs:101-111` is the only call site that omits the query, and `query` is in scope. Outcome: D15.
