# Plan — Attribute descriptions rendered as an [i] tooltip

**Issue:** [#348](https://github.com/MintPlayer/MintPlayer.Spark/issues/348)
**PRD:** `docs/issue_348_PRD.md`
**Branch:** `feat/issue-348-attribute-descriptions`
**Base:** `master` @ `0ea11110`
**Release:** `10.0.0-preview.70` · `@mintplayer/ng-spark` + `@mintplayer/ng-spark-auth` `22.9.0`

---

## Milestones

| M | Title | Breaking? |
|---|---|---|
| M0 | Spikes S1–S5 | — |
| M1 | Model: `description` on `EntityAttributeDefinition` + TS mirror | no |
| M2 | Generator: `SparkAttributeDescriptionAttribute` + `AttributeDescriptionsGenerator` | no |
| M3 | Sync: `[Description]` + generated summary seeding, rule ii, fixed point | no |
| M4 | Wire: `QueryColumn.description`; `PersistentObjectAttribute.description` only if S1 says so | no |
| M5 | Client: `spark-attribute-description` component in every label site | no |
| M6 | Demo: DemoApp (flag on) + HR (flag off) exercise every surface; Release build inspected | no |
| M7 | Tests: one batched run (generator snapshots + server + ng-spark + E2E) | — |
| M8 | Docs: `guide-attribute-descriptions.md`, release notes, PRD status | — |
| M9 | Versions | — |

M0 first; its answers shape M2 (S4), M3 (S2, S5), M4 (S1) and M5 (S3). M1 → M2 → M3 → M4 are server-side and sequential (M3 needs the attribute type from M2). M5 depends on M1/M4 for the TS types but the component can be built against a stubbed field in parallel with M2/M3. M6 needs M3 + M5. M7 runs once, after M6, per repo convention; intermediate milestones are verified by reading and type-checking only (`dotnet build`, `nx run ng-spark:build`).

**One PR.** Every milestone, every spike result, every incidental fix found along the way lands in the single PR for this issue. No follow-up PR.

## M0 — Spikes

Each can come back "no" and reshape its milestone. Record the verdict under the spike heading in this file before starting the milestone it shapes.

**Verdicts (2026-09-02).** None reversed a design decision; three sharpened one.

- **S1 → schema-only.** Every label site reads `EntityAttributeDefinition` (form, detail, AsDetail `<th>`, reference picker) or `QueryColumn` (grid). Nothing reads `PersistentObjectAttribute`; create and edit both take the entity type's attributes and use the loaded object for values only. M4 touched `QueryResultProjector` + `QueryColumn` and nothing on the per-object wire.
- **S2 → verify-model compared hashes only.** `Verify` reads `modelHashes.json` and recomputes; `description` is outside `StructuralAttributeFields` by design, so a stale `en` was invisible. Added `ModelSynchronizer.DescribeDescriptionDrift` + `VerifyAttributeDescriptionsAreCurrent`, exit code 3 like hash drift. `TranslatedString` serializes insertion order; the seeding code inserts `en` first when adding it and overwrites in place when present (the converter was left alone — reordering it would have churned every label in every app). DemoApp master was already a byte-identical fixed point; HR's `CarreerJob.json` was not (hand-edited key order), and is now.
- **S3 → no peer bump.** `tooltip/` first shipped in ng-bootstrap 14.10.3, was silently absent from 21.33.0–22.0.0 (renamed `ng-package.json`), and is back since 22.1.0; ng-spark's `^22.13.0` already covers it. `*bsTooltip`'s trigger is the element containing the template, so the `<span *bsTooltip>` sits directly inside the `<button>`. A `<button>` inside `<label for>` does not forward the click (interactive content), so the [i] lives inside the label.
- **S4 → trivia is the default path.** Under `DocumentationMode.None` (every project in this repo) `///` lines are plain `SingleLineCommentTrivia`, one per line, and `GetDocumentationCommentXml()` is `""`. Under `Parse`/`Diagnose` it is one structured trivia and the XML has the `<member name="P:…">` wrapper with `\r\n` and resolved crefs (`T:Fx.Company`, `P:Fx.Company.Name`, ``P:Fx.Box`1.Value``). `<inheritdoc/>` is not expanded. Both usable inside the syntax provider's transform without a `CompilationProvider`. The harness parsed with `Parse` by default and had no knob; `parseOptions` was added.
- **S5 → stripping confirmed, and a correction.** `[Conditional("DEBUG")]` drops assembly-level applications in Release (2 in Debug, 0 in Release; decided by the applying assembly's symbols; no compiler warning). Every path that writes the model runs Debug (CI verify passes `-c Debug`; `dotnet run` defaults to Debug); `docs/model-hash.md` had a stale `-c Release` snippet, now fixed. Correction to D3: the index-assembly catalog does **not** contain the entity library — descriptions are read from `property.DeclaringType.Assembly`, not from the catalog.
- **Found by M6, not a spike:** entity libraries reference only Abstractions, so no Spark generator ran there. The analyzer reference was added to `DemoApp.Library` and `HR.Library`; that exposed `GenerateIndexGenerator` emitting `AbstractIndexCreationTask` subclasses into a Raven-less compilation (CS0400), now gated on Raven being referenced.

### S1 — Which shape do the label sites read (shapes M4, M5)

1. For each site in PRD F4, read the host component's `.ts` and find the type of `attr` / `col` in the label `ng-template`: `spark-po-form.component.ts`, `spark-po-detail.component.ts`, `spark-query-grid.component.ts`, the AsDetail `asDetailColumns` pipe, `spark-reference-picker.component.ts`.
2. For every site whose object is `PersistentObjectAttribute` (per-object wire), check whether the host already holds the `EntityType` (from `GET /spark/entity-type/{id}`) and can look the definition up by `attr.name`.
3. Verdict: **schema-only** (M4 touches only `QueryResultProjector`) or **wire too** (M4 also touches `PersistentObject.cs`, `EntityMapper.cs:400/434`, `PersistentObjectAttributeJsonConverter` write + `KnownFieldNames`, `persistent-object-attribute.ts`).

Prefer schema-only if any lookup is cheap; prefer wire if it would mean threading a new input through more than one host.

### S2 — Verify-model semantics and fixed point (shapes M3)

1. In DemoApp, hand-edit `Person.json` to give one attribute `"description": { "en": "stale", "nl": "…" }` and give the property a `[Description("fresh")]`.
2. Run `dotnet run -- --spark-verify-model`. Does it report the `en` difference? If it only checks the structural hash, AC7 needs verify-model to also diff the full synchronized output (check `SparkDevelopmentExtensions.cs:58` and the verifier it calls).
3. Run `--spark-synchronize-model` twice; `git diff --stat` must be empty after the second run. Check `TranslatedString` key ordering after inserting `en` into a dictionary that already has `nl`: if the converter writes insertion order, decide on a canonical order (`en` first, then alphabetical) and make the converter or the seeding code enforce it, otherwise the second run is not byte-identical.

### S3 — Tooltip inside grid header and form label (shapes M5)

1. `git log --oneline -- libs/mintplayer-ng-bootstrap/tooltip` in `C:\Repos\mintplayer-ng-bootstrap` and match to the tag that first shipped it. Set `ng-spark`'s peer range to that or higher (currently `^22.13.0`; the workspace pins `^22.17.0`).
2. Prototype the D5 markup directly in `spark-query-grid.component.html:42-46` inside `*bsDatatableColumn="col.name; sortable: true"`. Check: overlay positions above the header, is not clipped by the datatable's scroll container, clicking the [i] does not sort (add `(click)="$event.stopPropagation()"` on the button if it does), Tab focuses the button and shows the tooltip, Escape closes it.
3. Same in `spark-po-form.component.html:45-50` inside `<label bsColFormLabel>`: a `<button>` inside a `<label for=…>` forwards clicks to the input — confirm whether that steals focus from the [i] and, if so, render the [i] as a sibling of the `<label>` rather than inside it.

### S4 — What the generator sees, with and without the flag (shapes M2)

1. In `tests/MintPlayer.Spark.SourceGenerators.Tests`, add a throwaway test that compiles a fixture entity with `///` on properties covering: plain summary, `<see cref="Company"/>`, `<see cref="Company.Name"/>`, `<see langword="null"/>`, `<para>`, `<c>`, `<paramref>`, `<inheritdoc/>`, a property on a nested type, a property on a generic type, a property split across two `partial` declarations, a property with a `//` (non-doc) comment above it.
2. Run it under `DocumentationMode.None` and `DocumentationMode.Diagnose`. Record for each case: what `GetDocumentationCommentXml()` returns; what `GetLeadingTrivia()` contains and whether the `///` trivia has a `GetStructure()`; what the resolved cref ID looks like.
3. Confirm the hybrid (structured first, trivia fallback) can be expressed as an incremental `CreateSyntaxProvider` over `PropertyDeclarationSyntax` with the semantic model, without a `CompilationProvider` dependency that would defeat incrementality.
4. Verdict feeds the D2 sanitizer rules and the snapshot fixtures for M2.

### S5 — `[Conditional("DEBUG")]` stripping and the sync build configuration (shapes M3, M6)

1. Put a prototype `SparkAttributeDescriptionAttribute` with `[Conditional("DEBUG")]` in Abstractions, hand-write one `[assembly: …]` in `DemoApp.Library`, build Debug and Release; inspect both DLLs (`System.Reflection.Metadata` or `ildasm`) — the Release one must contain zero applications, the Debug one exactly one.
2. Confirm `Assembly.GetCustomAttributes<SparkAttributeDescriptionAttribute>()` from the app's `--spark-synchronize-model` sees the Debug attribute when `DemoApp.Library` is a `ProjectReference`.
3. List where sync runs: `apps/*/Dockerfile`, `.github/workflows/*.yml`, `nx` targets, `docs/guide-*`. Record the configuration each builds with. If any sync path builds Release, the entity library there will contribute no descriptions; decide whether that path must build Debug or whether the info log line (D3) is enough.
4. Edge: an entity library consumed as a NuGet package is Release-built and carries no descriptions — document as expected behaviour (descriptions are seeded where the source is).

## M1 — Model field

- `libs/spark/MintPlayer.Spark.Abstractions/EntityTypeDefinition.cs`: `public TranslatedString? Description { get; set; }` after `Label`, with a `///` summary stating the semantic and pointing out the entity-level `Description` is the heading.
- `libs/node_packages/ng-spark/models/src/entity-type.ts`: `description?: TranslatedString;` after `label`.
- `ModelFileShape.StructuralAttributeFields` and `SparkModelShape`: **no change**; add a unit test asserting a description change does not change the file hash (AC6).
- Verify: `dotnet build`, existing model-shape tests still pass by reading.

## M2 — Generator

- `libs/spark/MintPlayer.Spark.Abstractions/SparkAttributeDescriptionAttribute.cs`: `[AttributeUsage(Assembly, AllowMultiple = true)] [Conditional("DEBUG")] sealed class SparkAttributeDescriptionAttribute(Type type, string property, string summary)`.
- `libs/source_generators/MintPlayer.Spark.SourceGenerators/Generators/AttributeDescriptionsGenerator.cs` + `.Producer.cs`, deriving from `IncrementalGenerator` like its siblings:
  - Syntax provider over `PropertyDeclarationSyntax`; keep only public instance properties whose containing type matches the entity predicate used by `PersistentObjectNamesGenerator`, and which are not `[IgnoreProperty]`.
  - Summary resolution per PRD D2 (structured XML first, trivia fallback), sanitizer per D2 rules, verdicts from S4.
  - Emit `SparkAttributeDescriptions.g.cs`: `[assembly: global::MintPlayer.Spark.SparkAttributeDescription(typeof(global::Ns.Person), "FirstName", "…")]`, sorted by type FQN then property name, string literal escaped via `SymbolDisplay.FormatLiteral`.
  - Guarded by `knowsSpark` like the registration generators; emits nothing when no property has a summary.
- No line in `SparkFullGenerator.Producer.cs` (attribute-only generator). Packaging is already covered by `MintPlayer.Spark.AllFeatures.csproj:39-53`.
- Tests (written now, run in M7): snapshot tests in `tests/MintPlayer.Spark.SourceGenerators.Tests/Generators` over the S4 fixture for both `DocumentationMode`s; AC13 cases (ignored / undocumented / `<inheritdoc/>` emit nothing; stable ordering).
- Verify: `dotnet build` of the SG project and of `apps/DemoApp` (generated file appears under `obj/.../generated`).

## M3 — Sync seeding

- `ModelSynchronizer.cs:575-577`: read `DescriptionAttribute`; new `AttributeDescriptionCatalog` (internal, per-assembly lazy cache over `Assembly.GetCustomAttributes<SparkAttributeDescriptionAttribute>()`, keyed `(Type, propertyName)`, base-type walk for inherited properties); `seed = descriptionAttr?.Description?.Trim() is { Length: > 0 } s ? s : catalog.Summary(property)`.
- Create branch (`:713-737`): `Description = seed is null ? null : TranslatedString.Create(seed)` (mirrors `Label`).
- Update branch (`:640-708`): `if (seed is not null) existing.Description = (existing.Description ?? new()).With("en", seed)` — preserve every other key; canonical key order per S2. No assignment when `seed is null`.
- One info log line per assembly with Spark entities but zero description attributes (D3 / AC12).
- `--spark-verify-model`: per S2, make sure a differing `en` surfaces as a diff.
- Tests (written now, run in M7): `ModelSynchronizer` tests for AC1–AC5, AC7, AC12, each running sync twice and asserting equality on the second pass; the test fixture assembly carries hand-written `[assembly: SparkAttributeDescription]` lines so the tests do not depend on the generator.

## M4 — Wire

- `Services/QueryResultProjector.cs:51`: `Description = a.Description`; `models/src/query-result.ts` `SparkCellColumn`/`QueryColumn`: `description?: TranslatedString`.
- Per S1 only: `PersistentObject.cs`, `EntityMapper.cs:400` + `:434`, `PersistentObjectAttributeJsonConverter.WriteSharedFields` + `KnownFieldNames`, `persistent-object-attribute.ts`. Converter round-trip test extended with the new field.

## M5 — Client component and sites

- New secondary entry point `libs/node_packages/ng-spark/attribute-description/` (`ng-package.json`, `index.ts`, `src/spark-attribute-description.component.ts|html|scss|spec.ts`). Standalone, `OnPush`, `input.required<TranslatedString | undefined>()`, imports `BsTooltipDirective`, `SparkIconComponent`, `ResolveTranslationPipe`. Renders nothing when the resolved text is empty. Markup per PRD D5; `white-space: pre-line`; `(click)="$event.stopPropagation()"` per S3.
- Drop into: `spark-po-form.component.html:45-50` (placement per S3), `:204`, `:270`; `spark-po-detail.component.html:86`, `:95`; `spark-query-grid.component.html:42-46`; `spark-reference-picker.component.html:65-68`. Leave the `[title]` bindings alone.
- `ng-spark/package.json`: peer `@mintplayer/ng-bootstrap` raised per S3.
- Specs (written now, run in M7): component spec (renders nothing / renders button / aria-label / stopPropagation / follows `currentLanguage`); one assertion per host spec that the [i] appears for a described attribute and not for an undescribed one (`spark-po-form.component.spec.ts`, `spark-po-detail.component.spec.ts`, `spark-query-grid.component.spec.ts`).
- Verify: `nx run ng-spark:build` (ngc with `-p`, per `reference_ngc_needs_project_flag`).

## M6 — Demo

- `DemoApp.Library.csproj`: `GenerateDocumentationFile` + `NoWarn CS1591` (structured path). `Person`: `/// <summary>` with `<see cref>` on one property; `[Description]` on another; `Person.json`: hand `fr`/`nl` on the first, JSON-only description on a third attribute.
- `apps/HR` library: one property with a `/// <summary>`, flag **off** (trivia path).
- Run `--spark-synchronize-model` for both apps, commit the resulting JSON; run again and confirm no diff; run `--spark-verify-model` clean.
- `dotnet build -c Release apps/DemoApp/DemoApp.Library`; confirm zero `SparkAttributeDescriptionAttribute` applications (AC12) with the S5 inspection script.
- Smoke in the browser via `dotnet run` (never `ng serve`, per CLAUDE.md): form, detail, grid header, AsDetail header; keyboard focus; language switch.

## M7 — Tests (single batched run)

`dotnet test` on `MintPlayer.Spark.SourceGenerators.Tests` and the affected server test projects; `nx run-many -t test -p ng-spark`; the E2E project if it covers the DemoApp Person pages. Record results here as `| Fact | Pins |` with actual counts. Re-run named tests in isolation before believing a flake (`reference_raventestdriver_disposal_trap`).

| Fact | Pins |
|---|---|
| `Hand_authored_description_survives_sync_unchanged` | AC1 |
| `Description_attribute_seeds_en_on_existing_and_new_attributes_and_preserves_other_languages` | AC2 |
| `Generated_summary_seeds_en_with_cref_rendered_as_simple_name_and_para_as_newline` (×2 modes, generator snapshot) | AC3 |
| `Description_attribute_wins_over_generated_summary` | AC4 |
| `Second_sync_pass_is_byte_identical` | AC5 |
| `Description_change_does_not_change_model_file_hash` | AC6 |
| `Verify_model_reports_stale_en` | AC7 |
| `Assembly_without_description_attributes_is_not_an_error_and_logs_once` | AC12 |
| `Ignored_undocumented_and_inheritdoc_properties_emit_nothing_and_output_is_sorted` (snapshot) | AC13 |
| `renders nothing without a description` / `renders a focusable button with tooltip` | AC8 |
| `click on the description button does not bubble` | AC9 |
| `renderer receives description on attribute input` | AC10 |
| `tooltip text follows currentLanguage` | AC11 |

## M8 — Docs

- `docs/guide-attribute-descriptions.md`: the JSON field, the three authoring surfaces, the `en`-ownership rule, the generator and `[Conditional("DEBUG")]` (why Release ships nothing, why sync must run against a Debug entity build), the optional `GenerateDocumentationFile` upgrade for cref fidelity with the CS1591 note, what `<see cref>` becomes, the `<inheritdoc/>` limitation, the component and how a custom shell can reuse it.
- Link from `docs/guide-translated-strings.md` ("Where TranslatedString Is Used") and `docs/guide-custom-attribute-renderers.md`.
- `docs/release-notes-preview-70.md`.
- PRD status → Implemented; spike verdicts recorded above.

## M9 — Versions

`preview.69` → `preview.70` in all 22 publishable `.csproj` files. `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth` `22.8.0` → `22.9.0` in lockstep. Major stays `10` / `22` — neither targeted platform moved. CI publishes on merge; no manual publishing; check the version diff in review.
