# Spark 10.0.0-preview.61 — a query owns its chrome, selection rules are enforced, and M-3 is finished

**Packages:** every `MintPlayer.Spark.*` package → `10.0.0-preview.61`.
`@mintplayer/ng-spark` → **22.3.0**, with a new `@mintplayer/ng-spark/grid` entry point.
`@mintplayer/ng-spark-auth` is unchanged at 22.3.0.

**Issues:** [#308](https://github.com/MintPlayer/MintPlayer.Spark/issues/308),
[#309](https://github.com/MintPlayer/MintPlayer.Spark/issues/309).

This release contains **security fixes and behaviour changes**. The npm minor is a minor because
the major tracks Angular, not our API — see `CLAUDE.md`. Read the breaking changes below.

---

## Breaking

### 1. Authorization failures return 404, not 403 (audit M-3)

An **authenticated** caller who lacks rights on an entity type, query or custom action now receives
`404 Not Found`, with a body byte-identical to a genuine not-found, instead of `403 Forbidden`.
This closes an existence oracle: 403-versus-404 previously told an unauthorized caller whether a
record, type or query existed. Row-level denials already behaved this way; the type level now
matches.

**Unknown entity types answer the same way.** `GET /spark/po/Bogus` returns 401 to an anonymous
caller and 404 to an authenticated one. This is deliberate and it is the surprising part: without
it the status still discloses which model files exist and are queryable — a map of the
application's data surface, recoverable one probe at a time from the endpoint `/spark/types`
deliberately filters.

**Anonymous callers still get 401** from `/spark/po/*`, `/spark/actions/*` and `/spark/lookupref/*`,
so `ng-spark-auth`'s login redirect keeps working. Catalogue endpoints (`/spark/types`,
`/spark/queries`, …) answer 404, as they already did — a 401 there would bounce every anonymous
visitor to the sign-in page just for loading a page.

**No client change is required.** Nothing in `ng-spark`, `ng-spark-auth` or the demos branched on
403.

**`MintPlayer.Spark.Client` consumers:** a call that previously threw `SparkClientException(403)`
now returns `null` (or throws with `StatusCode = NotFound`). Code catching `Forbidden` to
distinguish "denied" from "missing" can no longer make that distinction — by design.

### 2. `/spark/queries/{id}/execute` authorizes before parsing `?sortColumns=`

An unauthorized caller could previously enumerate an entity's attribute names by watching
400-versus-403 from the sort-column parser. Authorization now runs first.

### 3. `selectionRule` is enforced server-side

A rule that was decoration is now a gate: a request violating it gets **400**. Scoped to the query
path — an action invoked from a detail page names a parent rather than a selection, so
`showedOn: "both"` actions such as Fleet's `CarCopy` are unaffected there.

**A malformed rule now fails at configuration load** rather than silently permitting everything.
`"1-5"`, `"*"` and `"=abc"` are rejected. If your `customActions.json` contains one, the app will
refuse to start until it is fixed.

**Omitting `selectionRule` means no requirement.** `docs/prd/custom-actions-prd.md` previously
claimed it defaults to `"=0"`; that was wrong and is corrected — `"=0"` is a predicate meaning
"exactly zero selected", which disables the action the moment anything is ticked.

### 4. Row rules now see custom action names

`ISparkRowRule<T>.IsAllowedAsync` has always taken an action name, but only `"Read"`, `"Edit"` and
`"Delete"` were ever passed. A custom action that names rows now passes **its own name**.

⚠️ **A row rule that ignores its `action` parameter now also gates custom actions.** That is
almost certainly what its author intended, but it is a live behaviour change: an action that
deliberately reached rows the user cannot edit — an "admin resync" running under a user principal —
will start being refused. A rule that switches on `action` must default to the restrictive arm;
returning `null` for an unrecognised name means *unrestricted*.

### 5. `showedOn: "query"` renders

The client filtered custom actions on `'list'`, a value the server never emits. An action authored
per the documentation — `"detail"`, `"query"` or `"both"` — rendered nowhere. It now renders.
Nothing can regress, because `'query'` matched nothing before.

### 6. `IDatabaseAccess` no longer resolves CLR types

`ResolveType` moved to `ISparkTypeResolver`. It was a model concern living in the data-access class
only because that was where a `Type` was first needed.

---

## Added

- **A query declares its own chrome.** Four new `SparkQuery` fields, all optional:
  `actions` (narrow which custom actions the query offers — display only, the grant is still the
  gate), `headerRenderer` + `headerRendererOptions` (a registered component replacing the header,
  resolved through the new `SPARK_QUERY_CHROME` token exactly as attribute renderers resolve), and
  `rowsNavigable` (suppresses the first-column detail link for queries whose rows are not
  documents). Declared on the query rather than passed by the host, because a sub-query is rendered
  automatically from `EntityTypeDefinition.Queries`, where there is no host to ask.
- **`spark-sub-query` is embeddable.** `showCard` for a chromeless embed, `headerTemplate` for a
  one-off host override, `reload()` and `reloadToken` for refreshing without destroying the
  component, and an `error` output.
- **Row selection.** Grids gain a checkbox column exactly when an action is selection-gated, and
  are otherwise pixel-identical.
- **`refreshQuery` is handled.** The server could always emit it; the client dropped it silently.
  `refreshOnCompleted` now also refreshes a detail page's sub-queries.
- **`@mintplayer/ng-spark/grid`** — shared grid internals consumed by both grid components.

## Fixed

- A sub-query whose load failed rendered **nothing at all** — no card, no message. The spinner was
  also unreachable on a first load. Both came from one `@if (query())` gating the whole template.
- `spark-query-list` rendered a **spinner forever** on a denied query: its metadata load was an
  async method called from `subscribe` with no `catch`, so the rejection never reached the error
  surface the component already had.
- A failed page fetch presented as an empty grid in `spark-sub-query`.
- Switching a sub-query's `queryId` could build a row link from the **previous** type and the
  previous permission.
- Navigating between query routes carried the previous route's action buttons and `canRead`.
- Null booleans rendered as unchecked (indistinguishable from `false`) in sub-query grids.
- Validation errors rendered as `[object Object]` on the create and edit pages.
- The `SparkQuery` clone in `Queries/Execute.cs` silently dropped `Description` — and would have
  dropped all four new fields.
- **Unbounded selection payloads.** `SelectedItems` had no cap, and the `IgnoreMaxRequests` figure
  that looked like a budget is only a log threshold — computed from the payload size, so it warned
  later the larger the abuse. Now capped at 200 regardless of any rule.
- Fleet's `CarCopy` button in the car list returned **500**; it now enforces its `"=1"` rule.

## Documentation

`docs/guide-custom-actions.md` is now normative for `selectionRule` and carries the full grammar.
It also corrects two false statements: that an action with no authorization configured is
"available to all users" (the default is deny), and the omission of the `Read/{Type}` prerequisite —
granting `CarCopy/Car` alone is **not** sufficient for an action that names rows, because every
named row is re-loaded through the row-gated read path first.
