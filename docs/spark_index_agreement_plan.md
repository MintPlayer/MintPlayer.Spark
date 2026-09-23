# Index/model agreement — implementation plan

Companion to [`spark_index_agreement_PRD.md`](spark_index_agreement_PRD.md).
**Status: not started.** Successor to PR #441.

## Order

SP1 first — it is the only item that can change whether this ships at all. SP2/SP3 gate the code.
M1–M3 are the feature; M4+ are the corpus migration, which is where the operational risk lives.

---

## Spikes

### SP1 — Measure the reindex cost of a `Fields`-only definition change ⚠️ **do this first**

Deploy an index, then redeploy it with only `Fields` changed (add one `Index(name, FieldIndexing.Search)`)
against a scratch database holding production-shaped data. Observe whether the index resets: watch
`IndexDefinition` staleness, entry count, and whether queries return partial results during the
rebuild.

**Why first:** if a `Fields`-only change forces a full rebuild, the migration for
`apps/CodeCoverage` is an operational event, not a code change — its `Commits` index backs the commit
list, history chart, sparklines and branch badges. That may mean shipping the base class without
migrating CodeCoverage, or scheduling it.

**Do not quote a number without running this.** The repo records the adjacent fact (renaming an index
rebuilds from scratch) but not this one.

### SP2 — Unify the two index descriptors *(gates M1, M2)*

`GeneratedIndexInfo` has collection/index/projection/isDefault; `HandWrittenIndexEntityInfo` has
neither the collection type nor `[DefaultIndex]`, though `DescribeHandWritten` already holds the index
`INamedTypeSymbol` two lines from both. Add the two fields, then put one shared shape over both
records so the whole 17-row graph exists inside a single generator.

Kill criterion: if the pools cannot share a shape without disturbing the `generatedNames` subtraction
at `GenerateIndexGenerator.cs:170-181`, keep them separate and pass the graph alongside.

⚠️ **Do not copy the base-type walk verbatim** — all three existing copies mishandle multi-map and
2-arg map-reduce. Fix it once, in the shared helper (see M6).

### SP3 — Prove an analyzer reads generator output *(gates M5)*

The repo asserts it in prose twice and **no test demonstrates it**. Before writing a rule that depends
on seeing a generated `VAccount`, write a fixture that runs the generator *and* the analyzer and
proves the symbol is visible.

Kill criterion: if generated trees are not reliably analyzed (host settings can skip them), every rule
must re-derive from `[GenerateIndex]` the way `DefaultIndexAnalyzer` deliberately does, and degrade
rather than go silent. Also confirm the **cross-project diagnostic drop** measured at
`ValueObjectCompletenessAnalyzer.cs:136-150` — entity library + app-owned index is exactly that
topology, and a dropped diagnostic is indistinguishable from a passing check.

---

## Milestones

### M1 — The base classes *(gated by SP2)*
`SparkIndexCreationTask<T>` overriding `CreateIndexDefinition()` to call a `protected virtual
ConfigureSparkFields()`. New package (`Abstractions` must not gain `RavenDB.Client`).
**No `StoreAllFields`** in the base (D4). Skip any field already present in `IndexesStrings` so a
hand-written `Index(...)` in `OnInitialize()` is not silently overwritten.

### M2 — The generator emits the override instead of a private method
Same body as `IndexSearchFields()` today, `private void` → `protected override void`. Keep emitting
`IndexSearchFields()` alongside for one release so existing call sites compile; have the override call
it, or emit both.

### M3 — Migrate the four indexes that can participate
`Cars_Overview`, `Companies_Overview`, `Company_Cars`, `People_Overview` — all DemoApp, all already
`partial`, all with a `[FromIndex]` projection, and **no production data**. This is the safe proving
ground: the generated definition must come out byte-identical apart from the intended change.

### M4 — `libs/` indexes *(after SP1)*
Five indexes, none `partial`, none with a projection. They declare no `Index(...)` today, so a base
class that emits nothing for them should produce a **byte-identical definition** — verify that rather
than assume it. Touching `libs/` fires the CI version-bump gate and republishes packages.

### M5 — Analyzer: the uncalled `IndexSearchFields()` *(gated by SP3)*
~30 lines, reusing `SortCompanionAnalyzer`'s constructor-walk helpers (`:107-115`, `:174-185`): if the
index is `partial`, has `[Search]`-bearing projection fields, and no `IndexSearchFields` identifier
appears in its constructor, warn. Worth shipping **even if M1 is deferred** — it closes the hole with
no runtime, packaging or reindex cost.

Two more rules for claims that cannot be honoured, neither detected today:
- `canSort`/`canFilter: true` on a **collection** attribute (ordering by a collection is undefined,
  and equality against one silently becomes `== null`).
- `canSort: true` on a **`TranslatedString`** — the generator replaces the field with `{Name}_{lang}`
  per language while `ResolveSortProperty` only ever tries `requested + "Sort"`, so the flag resolves
  to a property that does not exist.

### M6 — Fix the multi-map / 2-arg base-type walk
`AbstractMultiMapIndexCreationTask<T>`'s argument is the **reduce result**, not a collection type, and
`AbstractIndexCreationTask<TDocument, TReduceResult>` derives from
`AbstractGenericIndexCreationTask<TReduceResult>` — *not* from `AbstractIndexCreationTask<TDocument>`.
So a real 2-arg map-reduce index resolves to a null collection type and **is never registered**, while
RavenDB still deploys it.

Three near-duplicate copies with different acceptance rules: `IndexCatalog.cs:214`,
`DefaultIndexAnalyzer.cs:140`, `ProjectionPropertyAnalyzer.cs:140`. Consolidate into one helper.

⚠️ Also fix `DefaultIndexAnalyzerTests.cs:22-24`, which stubs
`AbstractIndexCreationTask<TDocument, TReduceResult> : AbstractIndexCreationTask<TDocument>` — a
hierarchy RavenDB does not have, so its map-reduce test proves nothing.

Latent today: no index in the repo is multi-map or 2-arg.

### M7 — CodeCoverage, last and only if SP1 says it is safe
`Commits_ByRepository` is not `partial`, has no projection, and is guarded by
`CommitIndexShapeGuardTests` against ever gaining `StoreAllFields`. Migrate only if the base class is
proven to emit exactly its current definition.

### M8 — Two unrelated model gaps found during the investigation
- `PullRequestFeedback` has a generated index and projection but **no model file at all**.
- `Commit` has no `queryType`/`indexName`, so a `Database.*` query over it reads the **raw
  collection** rather than `Commits_ByRepository`.

Both pre-date this work; neither is urgent; both are cheap to fix while in the area.

---

## Safe migration order

DemoApp (no production data) → `libs/` (small collections, definitions should be unchanged) →
CodeCoverage **last**. Never the reverse.

## Carried context

- Measured RavenDB ground truth: PRD §2. **The Map is the gate.**
- Why the capability flags are not the input: `subquery_column_filters_PRD.md` §3.10.3 — rejected with
  evidence, do not re-litigate.
- Why synchronize must not write the flags: same file, §3.9.1.
