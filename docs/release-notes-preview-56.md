# Release notes — `10.0.0-preview.56`

Packages: all `MintPlayer.Spark.*` at `10.0.0-preview.56`; `@mintplayer/ng-spark` at `22.1.0`.

One issue: [#279](https://github.com/MintPlayer/MintPlayer.Spark/issues/279) — query-declared index
bindings replace `IndexRegistry`'s ambient collection-type resolution. **One Spark query = one RavenDB
index.** Successor to #272: preview.55 made the ambient default *deterministic*; this release makes the
binding *declared* and deletes the ambient mechanism.

## Migration (one command)

Run `dotnet run --spark-synchronize-model` once after upgrading and commit the result. Effects:

- every minted `Database.*` query gains an explicit `"indexName"` (the entity's default index);
- `"useProjection"` disappears from model JSON (it was dead on both server and client);
- `modelHashes.json` is rewritten — a query's `indexName` is now **structural** (hashed), because the
  runtime resolves the index through it, so a later hand-edit trips `--spark-verify-model` and the
  startup gate.

Until you re-sync, the runtime behaves identically: a query without `indexName` falls back to the entity
file's `queryType`/`indexName` binding, and old model files keep their old hashes (un-stamped queries
contribute no hash lines).

**If an entity has several projection-bearing indexes**, mark the one that shapes its model file with the
new `[DefaultIndex]` attribute (on the index class). Zero or several markers fail synchronize/verify/startup
with an error naming the candidates — the framework no longer guesses (ordinal-min is gone). A
`[GenerateIndex]` entity's generated index carries the marker automatically; opt out with
`[GenerateIndex(IsDefault = false)]` when a hand-written index is the intended default.

## How resolution works now

```
query.indexName  →  entity file's queryType/indexName  →  raw collection
                          └── both resolve through the name-keyed IIndexCatalog
```

- **A declared `indexName` is authoritative.** Previously it was silently overridden whenever the entity
  had any registered index — the query ran against the ambient default's index and projection no matter
  what the model said. Now the named index (and its `[FromIndex]` projection, via `ProjectInto`) is used;
  an unknown name is an **error**, never a silent raw-collection query with null computed fields.
- **The PO-list path reads the model.** `EntityTypeDefinition.QueryType`/`IndexName` — written and hashed
  since preview.44 but never read back — are now load-bearing.
- **The synchronizer maintains query bindings** with stored==derived provenance: empty values are stamped;
  a value naming a known non-default index is authored and preserved; a value naming a dead index
  (renamed/removed) is retargeted to the default (or cleared) with a console note.
- Sorting on a column the resolved projection lacks logs a warning and skips the column (previously a
  silent drop).

## Breaking changes (preview policy: absorb churn now)

1. **`IIndexRegistry` is deleted** — interface, `IndexRegistration`, retain-all + ordinal-min machinery,
   and the preview.55 plural API. The replacement is `IIndexCatalog`: name-keyed
   (`GetByIndexName`), frozen after startup population, with `GetDefaultForCollectionType` enforcing the
   `[DefaultIndex]` rules at freeze — so runtime startup, `--spark-synchronize-model`,
   `--spark-verify-model` and the test-host hash writer all reject an ambiguous default identically.
2. **`SparkQuery.UseProjection` is deleted** (server model, wire, and the ng-spark TS model — hence
   `@mintplayer/ng-spark` 22.1.0). Nothing read it anywhere; whether a query projects is derived from
   whether its resolved index has a `[FromIndex]` projection. `ProjectInto` still runs whenever a
   projection exists — stored/computed index fields keep flowing.
3. **Two CLR index classes with the same name now throw at catalog build** (they would deploy over the
   same RavenDB index). Previously first-wins with a console line.
4. **New diagnostic SPARK009** (Error): two `[DefaultIndex]` claims over one collection type in a
   compilation — including the implicit claim a `[GenerateIndex]` entity makes for its generated index.
   The catalog freeze remains the authoritative cross-assembly check.
5. `SparkEndpointFactory` gained an optional `configureIndexCatalog` hook for arming fixture indexes
   before the catalog freezes (test hosts only).

## Why

A collection entity can legitimately back dozens of indexes (a mature comparable app has 17 on one
entity). With declared bindings, coexistence needs no tiebreaker: put `[GenerateIndex]` on an entity next
to a hand-written index, give each query its `indexName`, and both grids are correct — the #272
motivating case, now pinned by an integration test.
