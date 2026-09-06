# Plan — One read pipeline

Implementation plan for [`query_pipeline_PRD.md`](query_pipeline_PRD.md). Lands in PR #367 alongside
[`actions_and_coverage_plan.md`](actions_and_coverage_plan.md). Nothing here is implemented yet.

**Sequencing rule:** M0 first, always. Three of the milestones below refactor enforcement code that
currently has no tests, and the two suites covering the most complex enforcement points install a
permissive double. Refactoring under that is how a silent fail-open ships.

---

## Spikes

### S1 — Is a Raven-only LINQ row filter evaluable in memory? *(blocks nothing; informs F5)*

`RowSecurity.cs:594` compiles the same `LambdaExpression` it hands to RavenDB. A filter using
`.In()` — as `apps/CodeCoverage/.../CommitActions.cs:28` does — is translated fine server-side. What
`filter.Compile()` does with it on the projection-fallback path is unknown.

**Method:** unit test, no server. Build `Expression<Func<Commit,bool>>` using `.In()`, compile,
invoke against an instance. Record the result — works, throws, or silently returns false. A silent
`false` is the bad outcome: it would deny rows rather than leak them, but on a path chosen by the
*shape of the result*, not by the policy.

**Decision it feeds:** if it throws or misbehaves, M6 gains a startup validation that compiles every
declared filter once and refuses a type whose filter is translation-only.

### S2 — Can parent scoping be declared in the model?

The design hinges on expressing "child rows belonging to this parent" declaratively so the framework
composes it. Spark already has `[Reference]` attributes and query-declared index bindings.

**Method:** take `Company` → `Car` from the DemoApp. Express the relation as a declared binding on
the query (child property ↔ parent id) and compose `child.CompanyId == parent.Id` as an
`Expression<Func<Car,bool>>` in the executor, without touching `CarActions`. Confirm it pushes down
to RavenDB rather than materializing.

**Fallback if it does not generalize:** keep the author-written source method, but pass `parent` to
the database branch too and make the *absence* of a declared relation the startup error (M2), rather
than inventing a relation language.

### S3 — What does a composed type's row policy look like?

A composed type has no document to re-judge, which is why enforcement is skipped. Establish whether
a filter over the *computed row shape* is coherent, or whether the honest answer is that composed
types declare `RowPolicy.OwnedByActions()` and the framework records that it is trusting them.

**Method:** take `MyAccountRow` from `apps/CodeCoverage` and try to express its real policy both
ways. Whichever is writable without lying is the design.

---

## M0 — Tests before refactoring *(prerequisite for M1, M5, M6)*

Nothing here changes behaviour. It makes the subsequent milestones safe.

1. **Remove `PermissiveRowSecurity` from `ExecuteCustomActionTests`** (`:584`) and
   `StreamingQueryExecutorUnitTests` (`:34`); replace with a real `RowSecurity` over a filter-only
   fixture. `FilteredDoc`/`FilteredDocActions` in `tests/.../_Infrastructure/GuardedDoc.cs` is the
   starting point — it already exists.
2. **Streaming row security, from zero.** Per-batch `FilterAsync`/`RedactAsync`; a shrinking
   allow-list stops delivery; `ResetRequestFilterCache` actually runs on the re-auth tick
   (`StreamingQueryExecutor.cs:119-131`) rather than only in isolation.
3. **The breadcrumb gap** carried over from the facets pass: `BreadcrumbResolverTests` stubs
   `IRowSecurity` with `ReturnsForAnyArgs(true)`, so it proves the plumbing and never that a real
   filter reaches the decision. Assert a referenced row the filter hides renders as the redacted
   placeholder. This surface has already had one real bypass (untyped load, fixed preview.71).
4. **A characterisation test for each of the four list pipelines** asserting what it does today —
   including `GET /spark/po/{type}` having no cap. These are the before-picture for M5.

**Done when:** the two suites run against real row security and still pass, and the four pipelines
have a recorded baseline.

---

## M1 — Close F1, F2, F3

Independent of the refactor, each a genuine fail-open, each currently untested.

- **F1 — `TotalItems` bypasses row filtering** for author-paged queries (`QueryExecutor.cs:94-107`).
  The author takes over search, sort, count and paging; row security is explicitly not transferable
  (`:755-758`). Either count after filtering, or refuse `SparkQueryPage` on a row-ruled type. Prefer
  the former; the latter is a usable fallback if the count cannot be made honest cheaply.
- **F2 — the custom-action row gate skips on unresolvable `clrType`** (`ExecuteCustomAction.cs:270`).
  Split the condition: composed type (no `clrType` declared) proceeds as today; declared-but-
  unresolvable **throws**, matching `QueryExecutor.cs:684-691` and every other path.
- **F3 — `ActionsResolver` degrades silently** (`:47-62`). A `FooActions` that is not
  `IPersistentObjectActions<Foo>` must be a loud startup error, not a fall-through to the permissive
  default. Separately, `FindActionsType` resolving by simple name across every loaded assembly
  first-match-wins (`:108-118`) must refuse on ambiguity rather than caching an arbitrary winner.

Each gets a test that fails before the fix.

---

## M2 — Parent scoping, declared and enforced

The headline defect. Depends on S2.

1. **Pass `parent` to `ExecuteDatabaseQueryAsync`.** It is absent from the signature entirely
   (`QueryExecutor.cs:473`) while the custom branch receives it three lines earlier.
2. **Compose parent scoping as a predicate**, ANDed with the child's row filter, pushed down —
   *parent-relation ∧ child row filter*, per S2.
3. **Refuse at startup** a query listed in a `persistentObject.queries` array that can be neither
   parent-scoped by a declared relation nor by an author-written source method. Message names the
   parent type, the alias and the two fixes. This is the breaking change flagged in the PRD.
4. **Wire or delete `OnLoadAsync`'s `parent`.** All three call sites pass `null`
   (`DatabaseAccess.cs:99`, `:143`, `:534`). With parent-scoping real, wire it — the sub-query
   parent load at `Execute.cs:126` is the natural supplier. If M2.2 lands without needing it, delete
   the parameter rather than shipping a documented lie.
5. **Streaming gets the parent too**, or refuses a sub-query alias with a streaming source — it
   already refuses composed+streaming (`SparkComposedQueries.cs:67-74`), so the precedent exists.

---

## M3 — Intent is visible: the reason enum

`SparkQueryContext` gains a `Reason` — list-opened, detail-tab, count, export, custom-action
selection, reference-picker. One hook serving a dozen callers is fine *provided* the caller's intent
is visible; the prior art threads exactly this and it is the part of its query design that works.

Also in this milestone, both defects in `OnQueryAsync` as it currently stands:

- **Put it on `IPersistentObjectActions<T>`** — it is neither on the interface nor marked
  `[NoInterfaceMember]`, unlike the four other off-interface members, so it is invisible through the
  interface-typed resolver overload. Ours, from earlier in this work.
- **Fire it for queries with no `entityType`** (`QueryExecutor.cs:191-193` short-circuits). Silent
  hook omission is the precise failure its own docs warn about.

---

## M4 — "No policy" ≠ "allow all", where it matters

F4 is a deliberate, documented fail-open and it stays — for types whose type-level grant is itself a
constraint. It is tightened exactly where the grant is not: **a type granted to a well-known group
(`anonymous`, `authenticated`) must declare a row policy explicitly**, even if that declaration is
allow-all. Startup refuses otherwise.

That is the population where a forgotten override is a public disclosure, and it is the population
the row-security guide already warns about (`guide-row-security.md:98-119`). `apps/CodeCoverage`
relies on this shape in production (`RepositoryActions.cs:56`) and will need a declaration —
verifying that the declaration it needs is the one it actually wants is part of this milestone, not
a rubber stamp.

Depends on S3 for what a composed type's declaration says.

---

## M5 — One sealed pipeline

The refactor. Requires M0.

Fold the four list paths onto one enforcement stage that cannot be bypassed by a caller forgetting
to call it — the prior art's storage provider dropped its security call by overriding a virtual, and
that must be structurally impossible here.

- `GET /spark/po/{objectTypeId}` (`DatabaseAccess.cs:151-215`) is a near-duplicate with no paging,
  no search, no sort, no `OnQueryAsync` and **no `take` cap** while `/execute` clamps to 1000. Fold
  it in or delete it; it is reachable and unbounded either way.
- Streaming keeps its own materialization (it must — it is a stream) but shares the enforcement
  stage rather than reimplementing `FilterAsync`/`RedactAsync` calls.
- Custom-action selection already re-runs the query with `restrictToIds`; the fallback path
  (`ExecuteCustomAction.cs:376`) is the odd one out.
- **Resolve the two-`Query`-authorization divergence** (`:485` vs `:528`, documented but unfixed at
  `SubQueryPruner.cs:81-85`). One answer to "which type is this query about".

---

## M6 — Row security invariants, stated and enforced

- **State the one-element-source invariant explicitly**: "can this user see this row" *is* "does
  this row survive the list filter". `ResolveEffectiveRuleAsync` (`RowSecurity.cs:512-545`) already
  implements it — the compiled filter is non-null whenever the filter is, independent of whether the
  per-row hook was overridden, so a filter-only type is gated on detail reads and list and detail
  cannot diverge. Make it an asserted invariant with a test, not an emergent property.
- **No silent pass-through on an unclassifiable element type.** The prior art's `default: return
  source;` is the shape to refuse. Spark's projection fallback already fails closed on an unreadable
  `Id` (`RowSecurity.cs:236-244`); audit the remaining arms for the same discipline.
- **Startup filter validation** if S1 says compiled evaluation is unsafe.
- **Symmetric write gates.** Update uses a dedicated side session (`DatabaseAccess.cs:278`), delete
  the shared request session (`:341`), and the comments claim they are the same shape. Either make
  them symmetric or correct the comment. `EnsureRowSaveAllowedAsync` bypasses the memoized
  `RowSecurity` service (`DefaultPersistentObjectActions.cs:302-307`), invoking hooks a second time
  per save outside the documented once-per-(type,action) cost contract.

---

## M7 — One `DisableActions`

Three surfaces sharing no mechanism: `PersistentObject.DisableActions`, `SparkQueryContext` /
`CustomQueryArgs.DisableActions`, and `IClientAccessor.DisableActions*` — the third **silently
dropped on the detail path**, because `Get.cs:37` returns `Results.Json(obj)` while only
`ClientResult.Envelope` attaches client operations.

One mechanism, reaching every path. It stays cosmetic by design — the action's own gate is the
authorization boundary and already re-checks on execute — but the name must not invite the prior
art's mistake of treating UI pruning as a boundary. Document it at the one remaining surface.

---

## M8 — The client-side inconsistencies

- **Reference links gate on `Query`, not `Read`** (`reference-link-route.pipe.ts:8` resolves against
  the `Query`-filtered catalogue) while the grid's first column correctly gates on `canRead`
  (`spark-query-grid.component.html:67`). Make them agree.
- **Reference pickers see 50 candidates, silently.** `executeQueryByName` forwards neither paging nor
  search (`spark.service.ts:76-82`), so every picker and AsDetail reference column truncates with no
  indication. It also costs a full `GET /spark/queries` per reference attribute, un-deduplicated.
- **N+1 parent loads.** A detail page with N sub-query cards runs the parent's `OnLoadAsync` N+1
  times (`Execute.cs:126`). Cache per request, or make cards lazy. Note this multiplies whatever M2
  adds to the load path.

---

## Verification

- Full framework suite, `CodeCoverage.Tests`, `ng-spark` — batched at the end, per repository
  policy, not per milestone.
- Both demo apps that use sub-queries boot and render a parent detail page with populated cards.
- `apps/CodeCoverage` boots with its new M4 declaration.
- The startup refusals in M2.3 and M4 are asserted by tests, including their message text — a
  refusal whose message does not name the fix is a worse defect than the one it replaces.

## Sequencing

```
S1 S2 S3  (parallel, cheap)
   │
  M0 ────────────────┐  (tests first — blocks M1, M5, M6)
   │                 │
  M1  M2  M3  M4     │  (independent of each other)
   │   │   │   │     │
   └───┴───┴───┴─── M5 ── M6 ── M7 ── M8
```

M1–M4 can land in any order once M0 is green. M5 is the one that needs everything before it, because
it moves the code the others fix.
