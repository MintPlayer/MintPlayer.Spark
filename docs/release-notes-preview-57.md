# Release notes — `10.0.0-preview.57`

Packages: all `MintPlayer.Spark.*` at `10.0.0-preview.57`; `@mintplayer/ng-spark` unchanged at `22.1.0`.

One issue: [#281](https://github.com/MintPlayer/MintPlayer.Spark/issues/281) — a row rule over a
`[FromIndex]`-projected entity could throw. **Upgrade is the whole migration**: no model change, no
`--spark-synchronize-model`, no client change, no API change.

## What was broken

A generic `Database.*` query over an entity carrying **both** a `GetRowFilterAsync` rule **and** a
`[FromIndex]` projection could return HTTP 500:

```
System.ArgumentException: Object of type 'Newtonsoft.Json.Linq.JObject' cannot be converted to
type 'YourApp.Entities.Thing'.
   at MintPlayer.Spark.Services.RowSecurity.FilterAsync(...)
```

The row filter is declared over the entity, and a projection carries only what the index stored — so
`FilterAsync` correlates each projected row back to its document and judges *that*. The batched reload
asked RavenDB for `object`, and the loaded value went straight to the consumer's compiled
`Expression<Func<TEntity, bool>>`, invoked reflectively. When the load produced anything other than
`TEntity`, the argument check threw before a single row was evaluated.

**Fail-closed, not a disclosure.** The request errored; no unfiltered rows were ever returned.

**Not a regression** — latent since row filters and `[FromIndex]` projections could first coexist
(reproduced identically on `preview.53`).

## Why it only bit some apps

Asking for `object` made the outcome depend on the *stored document*. RavenDB recovers the CLR type
from `@Raven-Clr-Type` when it can, so `LoadAsync<object>` usually returned a properly typed entity —
which is why the combination works in the Fleet demo and across the test suite. It degrades to a
`JObject` only when that metadata is **absent or names a type the process cannot resolve**:

- documents written by a raw `PutDocumentCommand`, bulk insert, Smuggler import, or ETL;
- an entity type renamed, or moved to a different assembly, after its documents were written.

Apps whose custom queries return the **base** entity type never tripped it even when those queries ran
through the index, because `resultType == entityType` means no projection correlation happens at all.

## The fix

The reload now names the entity type Spark declares, so it no longer depends on document metadata at
all — the failure condition stops existing rather than becoming rarer. Still exactly one batched
request per page (`MaxNumberOfRequestsPerSession` makes a per-row load fail past ~29 rows).

Two further changes ride along:

- **`RedactAsync` had the identical defect** for `GetProtectedAttributesAsync(string, TEntity)`, and is
  fixed in the same change. It was unreported only because the hook is rarely overridden. Fixing one
  alone would have been worse than fixing neither: RavenDB's session identity map returns a tracked
  instance regardless of the type a later load asks for, so whichever ran first would poison the other.
- **Breadcrumbs over the same rows improve as a side effect.** The typed reload primes the identity
  map, so the untyped loads that follow it in a request get the entity back rather than a `JObject`
  that `BreadcrumbResolver` would silently render as no breadcrumb.

Unchanged: the fail-closed branches. A projection with no readable `Id` still returns nothing, and a
projected row whose base document is missing or deleted is still dropped (and fully redacted).

## Upgrading

Bump the package versions. Nothing else.
