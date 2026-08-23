# 10.0.0-preview.62 — `security.json` moves into Spark core

`@mintplayer/ng-spark` 22.3.0 · [#310](https://github.com/MintPlayer/MintPlayer.Spark/issues/310)

Authorization stops being optional. `App_Data/security.json` is read by Spark core, every
application has one, and a missing or malformed file refuses startup.

The reason is that the previous design gave every application a state — *the developer has not
wired authorization up yet* — that had to be given a meaning, and neither meaning was any good.
Deny-everything reads as a broken app; allow-everything is a fail-open path nobody notices until
production. There is now nothing to choose, and an application that means to be open says so by
granting `*/*`, where the decision is visible in the file, printed at startup, and moved in a
committed baseline that code review sees.

---

## Breaking changes

| Gone | Do this instead |
|---|---|
| `spark.AddAuthorization()` | nothing — `AddSpark` registers it |
| `spark.AllowAnonymousAccess()` | grant `*/*` in `security.json` |
| `AuthorizationOptions`, including `DefaultBehavior` | the file is the configuration |
| `SparkFullOptions.Authorization` | — |
| `MintPlayer.Spark.Authorization.{Models,Services}` namespaces | `MintPlayer.Spark.Abstractions.Authorization` and `MintPlayer.Spark.Services` |
| `spark.UseGroupMembershipProvider<T>()` from the package | the same method, from `MintPlayer.Spark.Extensions` |

**Every application must ship `App_Data/security.json`.** Generate a starting point with:

```bash
dotnet run -- --spark-init-security
```

It grants nothing, refuses to overwrite an existing file, and carries the whole grammar in
comments — this is the only authoring support that exists.

There is **no compatibility shim**. Deleted means deleted; these packages are in preview.

### One query per URL

**A query alias now identifies exactly one query, and a collision refuses startup**, naming both
sides. It was previously a `Console.WriteLine` nobody reads: the losing query kept no reachable
alias, and nothing said so at the point of use.

⚠️ **Usually only one of the two aliases is written down.** An omitted alias is *derived* from the
query name (`GetStocks` → `stocks`), so it collides with an alias somebody declared on a different
query. The error message says which side was derived, because that author does not know they chose
one.

DemoApp is what it cost: `GetStocks` (`Database.Stocks`, a collection nothing ever writes) and
`StreamStocks` (the live grid its menu points at) both resolved to `stocks`, so `/query/stocks`
rendered an empty grid and the streaming query could not be reached at all.

Fix a collision by declaring an explicit, distinct `alias`. You cannot fix it by deleting a
`Database.*` query — those are derived from `SparkContext` properties and
`--spark-synchronize-model` writes them straight back. `--spark-verify-model` now checks this too,
because the model commands return before `builder.Build()`, so the startup gate never runs in CI.

Considered and rejected: letting a streaming and a non-streaming query share an alias with the
request's transport choosing. The client learns whether to open a socket from `isStreamingQuery` in
`GET /spark/queries/{alias}`, which is itself a plain HTTP request — so metadata would have to
answer for both variants at once, or the model would need per-query capability flags. Too
complicated for what it buys, and a URL stops naming one thing.

`SparkFullGenerator` changes in the same release, because it emitted the `AddAuthorization(...)`
call literally and every `AddSparkFull` application would otherwise stop compiling.

---

## Behaviour changes

**Combined actions expand symmetrically.** `deny EditNewDelete/Car` now denies Edit, New and
Delete. It used to deny the literal string and therefore nothing at all, and the loader refused
that shape rather than fixing it — so a file that previously failed validation may now load and
deny more than it did.

**Denials are evaluated before grants, across the caller's whole group set.** `grant Read/Car` in
one group plus `deny QueryReadEditNewDelete/Car` in another now resolves to **denied**. The old
per-right chain answered *allowed*, because the exact grant fired before the combined denial was
ever expanded. This is a set-based index on the loader rather than a fourth step in a chain,
specifically so the ordering cannot be re-broken.

**`Right.isImportant` does something.** It is a precedence tier that wins over everything,
denials included — not the audit marker its comment described and nothing implemented. Two
contradicting important rights resolve to the denial.

**Wildcards.** `*` on either half of a resource: `Read/*`, `*/Person`, `*/*`. The startup posture
report warns when the anonymous group holds one.

**The 401-vs-404 predicate** now asks whether the application has any way to sign in — the same
condition `UseSpark` uses to decide whether `UseAuthentication()` runs, so the two cannot
disagree.

**The posture report expands.** `QueryRead/Company` prints as two lines. Existing
`securityPosture.txt` baselines must be regenerated with `--spark-synchronize-security`.

---

## New

- **`docs/guide-authorization.md`** — the rights model, the four precedence tiers, group
  semantics, and what **`Query` without `Read`** does to a grid. That pair is the mechanism this
  release exists to make visible: the grid lists the rows and the first column is not a link,
  which is the correct model whenever a row cannot be loaded by id.
- **`--spark-init-security`**, and `--spark-verify-security` wired into all four demos with
  committed baselines. The posture gate had existed since preview.5x and no host called it.
- **Sub-query pruning** — a sub-query the caller cannot run is absent from the entity type rather
  than rendering a card that then 404s. A UX fix; `getQuery` already refused.
- **A request-scoped permission memo**, so `EntityTypes/List`, `GetAliases`, `ProgramUnits/Get`
  and `GetPermissions` stop asking the same question in a loop.
- **`SparkTestSecurity`** on `SparkEndpointFactory` — `Permissive` (default), `Empty`,
  `Granting`, `Denying`, `Without`, `FromFile`, `FromJson` — plus `SparkTestAccessControl` for
  the two cases a grant list cannot express. The factory asserts the host loaded the file it
  wrote.
- **A deny-all mirror suite** covering every Spark endpoint. It is the only thing that turns a
  deleted permission check into a red build, and it found three real bugs on its first run:
  `GET /spark/actions/{type}` answered 404 for an unknown type and 200 for a denied one (an
  existence oracle whose own comment claimed the opposite); `POST /spark/po/{type}` read the body
  before authorizing, so POSTing rubbish enumerated the entity types; and antiforgery ran first,
  which had been hiding both.

- **`AGENTS.md`, shipped in the packages and synced by MSBuild.** `MintPlayer.Spark` carries the
  framework specification and `MintPlayer.Spark.Testing` the testing one; each is copied into the
  consuming project on build, so a coding agent reads the same document the package ships rather
  than a stale wiki page. Opt out with `$(EnableSparkAgentsGuide)` /
  `$(EnableSparkTestingAgentsGuide)`.
- **`SparkSharedDatabase` + `SparkSharedTestDriver`** — an optional second test driver giving one
  database per test *class* instead of per test *case*, for the many fixtures that never write a
  document. Both drivers ship; neither replaces the other. The per-case `SparkTestDriver` remains
  correct for anything asserting on unscoped counts, fixed ids, or database-wide operations.
- **`RqlRecorder`** — captures emitted RQL and unsubscribes on dispose. The inline form
  (`Store.OnBeforeQuery += …`) never removes its handler, which is harmless only while the store
  dies with the test case.

---

## Client — `@mintplayer/ng-spark` 22.3.0

Carried over from the withdrawn #308 branch, minus everything authorization-shaped:

- The sub-query template is three explicit states. The spinner was unreachable on a first load, a
  first-load failure rendered **zero DOM**, and a failed reload left stale chrome behind.
- The unguarded `async` subscribe in both grids turned the deliberate 404 on a denied query into
  a **permanent spinner** that never reached the error surface the component already had.
- `showedOn: 'query'` is honoured. Both grids tested for `'list'`, a value nothing emits, so an
  action authored per the documentation rendered nowhere.
- Selection rules — one fixture drives the server and client parsers, because Vidyano's own two
  ports have drifted.
- A shared grid core, so the two grids cannot drift apart again.
- `reload()` / `reloadToken`, `[indeterminate]` booleans, permission-state reset, `[object
  Object]` fixes.

`rowsNavigable` was **dropped before shipping**: `Query`-without-`Read` already suppresses the row
link end to end, so the field was redundant. `SparkQuery.actions` and `headerRenderer` are held
back pending the #309 header-slot redesign.

---

## Upgrading

1. `dotnet run -- --spark-init-security`, then grant what the application needs. Remember that
   `anonymous` is not a floor: a right both an anonymous visitor and a signed-in user should have
   is two grants.
2. Delete `spark.AddAuthorization()` / `spark.AllowAnonymousAccess()`.
3. Re-namespace any direct use of `SecurityConfiguration`, `Right` or `IAccessControl`.
4. `dotnet run -- --spark-synchronize-security` and commit `App_Data/securityPosture.txt`.
5. Boot once. A duplicate query alias now refuses startup, naming both queries — declare an
   explicit `alias` on one of them.
6. Read the startup posture summary. It prints what an anonymous caller can reach,
   including when that is nothing.
