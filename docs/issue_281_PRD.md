# PRD — Issue #281: a row rule over a `[FromIndex]`-projected entity throws

**Status:** Implemented — all milestones done (see [plan](issue_281_plan.md)) · ships `10.0.0-preview.57`
**Issue:** [#281](https://github.com/MintPlayer/MintPlayer.Spark/issues/281)
**Branch:** `fix/issue-281-rowsecurity-typed-base-load`
**Plan:** [issue_281_plan.md](issue_281_plan.md)

## Problem

A generic query over an entity that has **both** a `GetRowFilterAsync` rule **and** a `[FromIndex]`
projection returns **HTTP 500**:

```
System.ArgumentException: Object of type 'Newtonsoft.Json.Linq.JObject' cannot be converted to
type 'Coverage.Entities.Repository'.
   at System.Delegate.DynamicInvokeImpl(Object[] args)
   at MintPlayer.Spark.Services.RowSecurity.<>c__DisplayClass15_0.<<ResolveEffectiveRuleAsync>b__0>d
   at MintPlayer.Spark.Services.RowSecurity.FilterAsync(...)
   at MintPlayer.Spark.Services.QueryExecutor.ExecuteDatabaseQueryAsync(...)
```

Found in MintPlayer/CodeCoverage while smoke-testing the `preview.56` upgrade; reproduced identically
on `preview.53`, so it is **not a regression** — it has been latent since row filters and `[FromIndex]`
projections could first coexist.

The rule is declared over the *entity*, so `FilterAsync` correlates each projected row back to its
source document and judges that. The batched reload asks RavenDB for `object`
(`RowSecurity.cs:191`), and the loaded value is handed straight to the consumer's compiled
`Expression<Func<TEntity, bool>>`, invoked reflectively. When the load yields anything other than
`TEntity`, `MethodInfo.Invoke`'s argument check throws before a single row is evaluated.

**Fail-closed, not a disclosure.** The request errors; no unfiltered rows are returned. That matches
the deliberate stance of the neighbouring branch at `RowSecurity.cs:165-173` (no `Id` on the
projection ⇒ return nothing, *"Loud and empty beats quiet and wrong"*).

## Investigation findings (three-agent sweep, 2026-08-20)

### F1 — The issue's trigger model was incomplete, and the gap is the whole difficulty

`LoadAsync<object>` does **not** usually return a `JObject`. RavenDB's `ConvertToEntity` reads
`@metadata.@Raven-Clr-Type`, and when `Type.GetType()` resolves that name it deserializes into the
*stored* CLR type — so `LoadAsync<object>` returns a real `Note`/`Car`/`Repository`. Only when the
metadata is **absent or unresolvable** does it fall through to
`DeserializeEntityFromBlittable(typeof(object), …)` and produce a `JObject`.

Measured directly against RavenDB 7.2.5:

```
LOAD<object> notes/1 -> Note                            # normal StoreAsync — metadata resolves
LOAD<object> notes/2 -> Newtonsoft.Json.Linq.JObject    # Raven-Clr-Type = "Gone.Away.Note, Gone.Away"
LOAD<object> notes/3 -> Newtonsoft.Json.Linq.JObject    # raw PutDocumentCommand, only @collection
LOAD<Note>   notes/1..3 -> Note, Note, Note             # the fix works for all three
```

Three consequences, all load-bearing:

1. **The demo apps and the existing suite cannot reproduce the bug as-is.** Every fixture document is
   written by `session.StoreAsync` in the same process, so its metadata always resolves.
   `RowFilterPushdownTests.A_projection_falls_back_to_the_batched_reload_with_the_compiled_filter`
   (`:269-307`) already drives *exactly* the failing call —
   `FilterAsync(session, projections, typeof(Note), typeof(VNote), "Query")` — and **passes**. The
   issue's "why the test suite missed it" section overlooks it. A regression test that merely bolts a
   projection onto a row-ruled fixture therefore **passes vacuously**; it must also seed a document
   whose CLR-type metadata does not resolve.
2. **`Fleet` / `Car` on `master` already satisfies both preconditions** (`CarActions.GetRowFilterAsync`
   + `[GenerateIndex]` → `VCar`, `Car.json` carrying `queryType`/`indexName`), and the E2E test
   `RowLevelAuthzTests.User_B_cannot_list_User_As_private_cars` drives it in CI. It is green — which
   is direct evidence that the metadata path, not the feature combination alone, is the trigger.
3. **The right framing of the fix is stronger than "use the right type argument".** The rule must be
   evaluated against the entity type Spark *declares*, never against whatever a stored document's
   metadata happens to claim. Loading as `entityType` removes the dependency on document metadata
   entirely, so the reproduction condition stops existing rather than becoming rarer.

Reproduced in this repo on `master`, with the production stack trace frame-for-frame:
`RowFilterProjectionReloadTests.A_projection_is_judged_as_the_entity_type_even_when_the_stored_clr_type_does_not_resolve`.

**What makes a consumer's documents lose resolvable metadata** — raw `PutDocumentCommand`,
bulk-insert of raw JSON, Smuggler import, ETL, or the entity type being renamed or moved to another
assembly after the documents were written. CodeCoverage's `Coverage.Entities.Repository` is in the
last category or one of the import categories; Spark installs no `FindClrType`/`FindClrTypeName`
convention override (`SparkMiddleware.cs:85-94`), so stock RavenDB behaviour applies.

### F2 — `RedactAsync` has the identical defect, and the two must be fixed together

`RowSecurity.RedactAsync` (`:312-314`) repeats the `LoadAsync<object>(ids)` shape and feeds the
result to `GetProtectedAttributesAsync(string, TEntity)` — also invoked reflectively, also
argument-checked, so it throws the same `ArgumentException`. The issue does not mention it. It is
unreported only because the hook is rarely overridden (`RedactAsync` returns at `:283` when it isn't)
and no demo app overrides it at all.

Fixing only one is worse than fixing neither, because of the identity map (F3): a `LoadAsync<object>`
that ran first poisons the later typed load of the same ids.

### F3 — RavenDB's session identity map returns the tracked instance regardless of `T`

Measured from `InMemoryDocumentSessionOperations.TrackEntity` IL (7.2.5): on a `DocumentsById` hit the
already-materialized entity is returned as-is and the requested type is never re-applied; the generic
wrapper then casts and converts an `InvalidCastException` into a friendlier throw. So load order
within a request matters.

On every path today `FilterAsync` runs **before** `RedactAsync` and before `BreadcrumbResolver`
(`QueryExecutor.cs:207→212→217`, `DatabaseAccess.cs:177→182→187`, `StreamingQueryExecutor.cs:130→140`).
Fixing `FilterAsync` therefore *primes* the map with correctly-typed instances that every later load
of the same ids reuses — a bonus fix for `BreadcrumbResolver.cs:121-128`, which does
`doc.GetType()` → `GetEntityTypeByClrType(...)` and silently renders no breadcrumb for a `JObject`.

One assumption remains unverified and is scheduled as a spike (S1): that a server-side
`ProjectInto<VRepository>` result is **not** tracked under the document id. Raven flags projections in
metadata and `QueryOperation.Deserialize` skips tracking for them, and the production symptom
(`JObject`, not `VRepository`) is consistent with that — but it was not measured in this repo. The
issue's own "detail 1" asks for exactly this.

### F4 — Blast radius: four untyped loads, two are bugs, two are correct

| Site | Verdict |
|---|---|
| `RowSecurity.cs:191` — `FilterAsync` | **The bug.** Fix. |
| `RowSecurity.cs:313` — `RedactAsync` | **Same latent bug** (F2). Fix in the same change. |
| `BreadcrumbResolver.cs:77` — root collection docs | Untyped in shape; only `doc.GetType()` is used. Not a crash risk. Silent-degradation risk only, and the F3 priming mitigates it on the query paths. Leave. |
| `BreadcrumbResolver.cs:116` — mixed referenced collections | **Genuinely heterogeneous**; a typed load is impossible by design (`PRD-Breadcrumbs.md:183`). Leave. |

No `LoadAsync<dynamic>` anywhere. `entityType` is non-null and a real document type at all four
callers of `FilterAsync`/`RedactAsync` (each caller guards or throws before reaching them), so it is
safe to close a generic over.

### F5 — The residual foreign-document case stays loud, and that is not a regression

On the custom-query and streaming paths `resultType` is arbitrary — its `Id` need not name a document
of `entityType`. With the typed load, `TrackEntity<T>` surfaces an `InvalidOperationException`; with
`LoadAsync<object>` today the foreign document came back and `DynamicInvoke` threw `ArgumentException`
on it anyway. Both are loud, so nothing gets quieter. Keeping it loud is consistent with
`RowSecurity.cs:168-172`.

## Requirements

- **R1** — `FilterAsync` evaluates the row rule against an instance of `entityType`, regardless of what
  `@Raven-Clr-Type` the stored document carries (or whether it carries one).
- **R2** — `RedactAsync` does the same for `GetProtectedAttributesAsync`.
- **R3** — The reload stays **one batched request** per call. RavenDB's
  `MaxNumberOfRequestsPerSession` (default 30) exists to catch the per-row shape; a projection-backed
  query over a row-scoped type would throw past ~29 rows.
- **R4** — Behaviour of the surrounding branches is unchanged: a projection with no readable `Id`
  still returns `[]` (`:165-173`); a projected row whose base document is missing or deleted is still
  dropped by `FilterAsync` and fully redacted by `RedactAsync`.
- **R5** — Id lookup stays case-insensitive. RavenDB's batched `LoadAsync` builds its dictionary with
  `StringComparer.OrdinalIgnoreCase` (verified in 7.2.5 IL); index-projected `Id` values can differ in
  case from the stored id.
- **R6** — No public API change; no model/JSON change; no consumer action required beyond upgrading.

## Acceptance criteria

1. A generic `Database.*` query over an entity with a row rule **and** a `[FromIndex]` projection
   returns the caller's rows — including when the base documents carry unresolvable or absent
   CLR-type metadata.
2. The same holds through the PO-list path (`DatabaseAccess.GetPersistentObjectsAsync`).
3. `RedactAsync` redacts correctly over a projection under the same metadata conditions.
4. A projected row whose base document was deleted is dropped, not thrown on.
5. Base documents come back as the entity type even when the projection query already touched the
   same ids in the same session (issue detail 1 / spike S1).
6. `filterSession.Advanced.NumberOfRequests` is still `1` for a filtered page (R3).
7. The existing row-security and `[FromIndex]` suites stay green; the E2E `RowLevelAuthzTests` stay
   green.

## Out of scope

- The two `BreadcrumbResolver` untyped loads (F4). The silent-`JObject` degradation there is real but
  distinct, is not a crash, and is mitigated on the query paths by F3 priming. Worth its own issue.
- Pushing a row filter down into a projection query (`ComposeRowFilterAsync`'s deliberate no-op).
  Unchanged: `FilterAsync` remains the gate for projections by design.
- Any change to how documents acquire `@Raven-Clr-Type`. The fix makes Spark independent of it.
