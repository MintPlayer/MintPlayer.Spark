# Release notes — `10.0.0-preview.55`

Packages: all `MintPlayer.Spark.*` at `10.0.0-preview.55`. No Angular package changes.

Four issues in one PR:
[#272](https://github.com/MintPlayer/MintPlayer.Spark/issues/272) (index registry coexistence),
[#273](https://github.com/MintPlayer/MintPlayer.Spark/issues/273) (complex fields fault Corax; breadcrumb
surface rework), [#275](https://github.com/MintPlayer/MintPlayer.Spark/issues/275) and
[#276](https://github.com/MintPlayer/MintPlayer.Spark/issues/276) (synchronizer preservation fixes).

**Breaking changes** (preview policy: absorb churn now):

1. **`[Breadcrumb("template")]` on a class no longer exists.** Display templates live exclusively in the
   model JSON (`"breadcrumb"` on the persistent object — same grammar, same reference recursion). Your
   templates are almost certainly already there: the synchronizer has been persisting them all along, so
   deleting the attribute usually requires nothing but deleting the attribute. `BreadcrumbAttribute` is now a
   property-level marker (see below). `SPARK003` (template names an ignored property) is retired with the
   attribute; the synchronizer still validates JSON templates on every run.
2. **`IIndexRegistry` gained `GetRegistrationsForCollectionType(Type)`** (plural, default-first). External
   implementors must add it; NSubstitute-style mocks pick it up automatically.
3. **`SPARK_INDEX_004` was removed** (it was declared but never reported, and its premise — one index per
   entity — is gone).

**Action required for #273 early adopters:** if you worked around the complex-field fault with an
`OnInitialize()` partial calling `Index(nameof(VX.Field), FieldIndexing.No)`, **delete that partial** — the
generator now emits the same call, and a duplicate `Index()` for one field throws at startup
(`Dictionary.Add` semantics in the RavenDB client).

---

## #272 — Several indexes over one entity coexist

`IIndexRegistry` used to keep a single slot per collection type: registering a second index for the same
entity silently rebound the slot, and the winner was decided by assembly scan order — including which
projection the generic grid used and what the model hash contained. The registry now retains **every**
registration. The generic query path resolves a deterministic **default**: the smallest index name under
ordinal comparison — name order rather than registration order, so moving a class within a file can never
silently flip the winner and trip the `modelHashes.json` startup gate. A warning at registration time names
the coexisting indexes and the chosen default. Non-default indexes stay fully usable via
`session.Query<TProjection, TIndex>()`, and their projections are correctly recognized as projections (they
no longer risk being emitted as entity model files).

## #273 — Complex fields: stored-not-indexed, plus the breadcrumb sort companion

A `[GenerateIndex]` entity with a complex-typed property (nested object, collection of non-scalars,
dictionary, user-defined struct) used to generate an index that **faults on every document on Corax**
(`NotSupportedInCoraxException`), leaving the index empty and the grid blank — with no compile-time signal,
and only once real data arrived (null complex values index fine, so an empty dev database looks healthy).

Now:

- Complex fields are mapped, stored (`StoreAllFields`) and declared `FieldIndexing.No` — the AsDetail column
  keeps rendering; the index is healthy. `SPARK_INDEX_010` (Warning) tells you the column cannot be filtered
  or sorted. Sorting/filtering such a field does not error — it silently returns unordered/empty results.
- **To make a singular complex column sortable**, mark a property of its type with the new property-level
  `[Breadcrumb]` — typically a null-safe computed property hidden with `[IgnoreProperty]`:

  ```csharp
  public class Address
  {
      public string Street { get; set; } = "";
      public string City { get; set; } = "";

      [Breadcrumb, IgnoreProperty]
      public string Crumb => $"{Street}, {City}";
  }
  ```

  Computed get-only properties persist into the document JSON, so the generated `{Name}Sort` companion is a
  plain member access (`item.Address.Crumb`) and multi-level composition is ordinary C# — a parent's computed
  crumb can reference a child's. **The getter must be null-safe**: it runs during serialization of every save
  (and every session dirty-check), so a throwing getter makes the entity unsavable. Documents saved before the
  property existed grow the field on their next save.
- The marker also feeds display: the synthesized default template for a marker-carrying type is
  `"{MarkedProperty}"`, embedded objects without a registered template render their marked property, and a
  sync-time warning flags an authored template that omits the marker (sort and display would silently
  disagree). Template tokens naming an **embedded complex property** (`"{Customer}"` where `Customer` is a
  nested object) now recurse into the embedded type's breadcrumb — previously they rendered the CLR type
  name.
- New diagnostics: `SPARK_INDEX_011` (multiple marked properties — ordinal-min wins), `SPARK_INDEX_012`
  (marker chain unusable: `[Reference]` id, collection, cycle, or companion-name collision), and a standalone
  placement analyzer — `SPARK007` (marker inside a `[FromIndex]` projection has no effect) and `SPARK008`
  (marker on a `[Reference]` id / collection / `TranslatedString`).
- Collections of complex elements have no single sort value and stay stored-not-indexed regardless of
  markers.
- `Demo/HR` is the worked example: `Person` is now `[GenerateIndex]` (the hand-written `People_Overview` is
  deleted; its `FullName` concat became a computed `[Search]` property), `Address` carries the marker, and
  `Jobs` demonstrates the stored-not-indexed fallback.

Note: whether the verbatim complex map faults is **engine-dependent** — Lucene tolerates it, Corax does not.
The in-repo regression tests pin `SearchEngineType.Corax` explicitly.

## #275 — A hand-set `query` on a non-`[Reference]` attribute survives synchronize

`ModelSynchronizer` nulled it on every run. Now the assignment is provenance-gated: a `[Reference]`
attribute's `query` still re-derives every run; removing `[Reference]` clears the stale derived value (with a
console note); a `query` on an attribute that never was a reference could only have been authored — it is
preserved.

## #276 — Renaming a `SparkContext` property no longer strands its query

Previously the old query kept its dead `"Database.OldName"` source (silently returning no rows forever) and a
duplicate was minted for the new name. Now, when exactly one `Database.*` source goes dead and exactly one
property is unclaimed, the existing query is **retargeted in place** — same `id` (program units keep
working), authored sort columns/alias/settings intact, conventional `Get{Name}` names follow the rename.
Ambiguous cases (several same-typed properties renamed at once) fall back to today's behavior plus warnings;
an unpairable dead source is kept and warned about, never deleted. `Custom.*` queries and hand-authored
`indexName`/`useProjection` are never touched.
