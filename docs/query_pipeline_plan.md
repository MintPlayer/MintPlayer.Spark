# Plan — One read pipeline

Implementation plan for [`query_pipeline_PRD.md`](query_pipeline_PRD.md), whose **Decisions** table is
authoritative — this plan implements those answers and does not re-open them.

**Lands in its own PR, after #367 merges and deploys.** #367 carries the dead-queue fix; it is not
held behind a framework redesign. Related work: [`actions_and_coverage_plan.md`](actions_and_coverage_plan.md).

**Sequencing rule:** M0 first, always. Several milestones refactor enforcement code that has no
tests, and the two suites covering the most complex enforcement points install a permissive double.
Refactoring under that is how a silent fail-open ships.

---

## Spikes

### S1 — Is a Raven-only LINQ row filter evaluable in memory? *(informs F5 and M6)*

`RowSecurity.cs:594` compiles the same `LambdaExpression` it hands to RavenDB. A filter using `.In()`
— as `apps/CodeCoverage/.../CommitActions.cs:28` does — translates fine server-side. What
`filter.Compile()` does with it on the projection-fallback path is unknown.

**Method:** unit test, no server. Build the expression, compile, invoke against an instance. Record
which of three happens: works, throws, or silently returns `false`. **The silent `false` is the bad
outcome** — it denies rather than leaks, but on a path chosen by the *shape of the result* rather
than by the policy, so a type would filter correctly until someone added a projection.

**Feeds:** if unsafe, M6 gains startup validation that compiles every declared filter once and
refuses a type whose filter is translation-only.

### S2 — What does an entity-less type's row-policy declaration say?

Decision 2 settled the *model*: the actions class is trusted and the framework records it. What
remains is the wording of the declaration and what startup prints. Take `MyAccountRow` and `Home`
from `apps/CodeCoverage` and write both declarations for real.

**Watch for:** if `MyAccountRow` turns out to need per-user scoping it does not have today, that is a
live production finding, not a design question — stop and treat it as one.

---

## M0 — Tests before refactoring *(blocks M1, M5, M6, M9)*

Nothing here changes behaviour. It makes everything after it safe.

1. **Remove `PermissiveRowSecurity`** from `ExecuteCustomActionTests` (`:584`) and
   `StreamingQueryExecutorUnitTests` (`:34`); replace with real `RowSecurity` over a filter-only
   fixture. `FilteredDoc`/`FilteredDocActions` in `tests/.../_Infrastructure/GuardedDoc.cs` already
   exists and is the starting point.
2. **Streaming row security, from zero.** Per-batch `FilterAsync`/`RedactAsync`; a shrinking
   allow-list stops delivery; `ResetRequestFilterCache` actually runs on the re-auth tick
   (`StreamingQueryExecutor.cs:119-131`) rather than only in isolation.
3. **The breadcrumb gap** carried over from the facets pass: `BreadcrumbResolverTests` stubs
   `IRowSecurity` with `ReturnsForAnyArgs(true)`, so it proves the plumbing and never that a real
   filter reaches the decision. Assert a referenced row the filter hides renders as the redacted
   placeholder. This surface has already had one real bypass (untyped load, fixed preview.71).
4. **Characterise all four list pipelines** as they behave today — including `GET /spark/po/{type}`
   having no cap. The before-picture for M5.

**Done when:** both suites run against real row security and still pass, and the four pipelines have
a recorded baseline.

---

## M1 — Close F1, F2, F3

Three genuine fail-opens, each currently untested. Each gets a test that fails before the fix.

- **F1 — `TotalItems` bypasses row filtering** (`QueryExecutor.cs:94-107` vs `:871`). **Decision 9:
  make the count honest** — count after filtering rather than trusting the author's total. This costs
  author-paging its cheap count, which is the price of the feature being correct on a row-scoped
  type. Row security was never transferable to the author; the code says so at `:755-758`.
- **F2 — the custom-action row gate skips on unresolvable `clrType`** (`ExecuteCustomAction.cs:270`).
  Split the condition: a composed type (no `clrType` *declared*) proceeds as today; a
  declared-but-unresolvable one **throws**, matching `QueryExecutor.cs:684-691` and every other path.
- **F3 — `ActionsResolver` degrades silently** (`:47-62`). A `FooActions` that is not
  `IPersistentObjectActions<Foo>` becomes a loud startup error. Separately, `FindActionsType`
  resolving by simple name across every loaded assembly first-match-wins (`:108-118`) must refuse on
  ambiguity rather than caching an arbitrary winner.

---

## M2 — The parent reaches every branch

**Decision 3: pass the parent, require an actions method. No relation language.**

1. **Give `ExecuteDatabaseQueryAsync` the parent.** It is absent from the signature entirely
   (`:473`) while the custom branch receives it three lines earlier.
2. **A query used as a sub-query routes through the actions class**, so the author scopes it with
   the idiom that already works (`apps/DemoApp/.../CarActions.cs:34-38`).
3. **Refuse an un-parent-scopable sub-query alias** — analyzer at build time (M8), startup
   validation as the backstop. The message names the parent type, the alias, and the fix.
4. **Wire or delete `OnLoadAsync`'s `parent`.** All three call sites pass `null`
   (`DatabaseAccess.cs:99`, `:143`, `:534`). The sub-query parent load at `Execute.cs:126` is the
   natural supplier. If nothing needs it once M3 lands, **delete the parameter** rather than shipping
   a documented lie.

---

## M3 — `OnQueryAsync`, reshaped

**Decision 5: no reason enum.** The hook receives the query and a `PersistentObject? parent`;
`parent != null` is a detail tab, `null` is a query page. That is the whole intent signal, and M2
makes it uniform across both source branches.

Recorded limit, so nobody designs against it later: a standalone query page and a custom-action
selection re-run both arrive with `parent == null` and are indistinguishable. Fine for withholding
actions; the hook can never be where export-versus-list behaviour is decided.

Also here, both defects in the hook as it stands today — ours, from the preceding work:

- **Put it on `IPersistentObjectActions<T>`.** It is neither on the interface nor marked
  `[NoInterfaceMember]`, unlike the four other off-interface members, so it is invisible through the
  interface-typed resolver overload.
- **Fire it for queries with no `entityType`** (`:191-193` short-circuits). Silent hook omission is
  the precise failure its own documentation warns about.

---

## M4 — Row policy is declared

**Decision 7: refuse, loud and early.**

- **A type granted to a well-known group (`anonymous`, `authenticated`) must declare a row policy**,
  even if that declaration is allow-all. Startup refuses otherwise. That is the population where a
  forgotten override is a public disclosure, and the row-security guide already warns about it
  (`guide-row-security.md:98-119`).
- **An entity-less type declares that its actions class owns row security** (decision 2), and
  startup records it rather than silently skipping enforcement. S2 settles the wording.
- `apps/CodeCoverage` relies on the well-known-group shape in production
  (`RepositoryActions.cs:56`). Confirming the declaration it *needs* is the one it actually *wants*
  is work in this milestone, not a rubber stamp.

F4 otherwise stays: "no rule ⇒ unrestricted" is correct where the type-level grant is itself a
constraint.

---

## M5 — One sealed pipeline

The refactor. Requires M0.

- **Delete `GET /spark/po/{objectTypeId}`** (decision 6) along with
  `IDatabaseAccess.GetPersistentObjectsAsync` and `SparkService.list`. Its single caller,
  `apps/WebhooksDemo/.../github-projects.component.ts:63`, moves to `/execute`. Touches the
  `DenyAllEndpointMirrorTests` list and `DatabaseAccessIntegrationTests` / `DatabaseAccessRowLevelAuthzTests`.
- **Streaming joins the pipeline** (decision: bring it in). It keeps its own materialization — it is
  a stream — but shares the enforcement stage instead of reimplementing the `FilterAsync`/
  `RedactAsync` calls, and gains the parent and the hook.
- **The custom-action selection fallback** (`ExecuteCustomAction.cs:376`) is the remaining odd path.
- **Resolve the two-`Query`-authorization divergence** (`:485` vs `:528`), documented but unfixed at
  `SubQueryPruner.cs:81-85`. One answer to "which type is this query about".

Enforcement must not be a virtual a caller can forget — the prior art's storage provider dropped its
security call by overriding one, and that must be structurally impossible here.

---

## M6 — Row-security invariants, stated and enforced

- **Assert the one-element-source invariant**: "can this user see this row" *is* "does this row
  survive the list filter". `ResolveEffectiveRuleAsync` (`RowSecurity.cs:512-545`) already implements
  it — the compiled filter is non-null whenever the filter is, independent of whether the per-row
  hook was overridden, so list and detail cannot diverge. Make it a tested invariant, not an
  emergent property.
- **No silent pass-through on an unclassifiable element type.** The prior art's `default: return
  source;` is the shape to refuse. Spark's projection fallback already fails closed on an unreadable
  `Id` (`:236-244`); audit the remaining arms for the same discipline.
- **Startup filter validation** if S1 says compiled evaluation is unsafe.
- **Symmetric write gates.** Update uses a dedicated side session (`DatabaseAccess.cs:278`), delete
  the shared request session (`:341`), while the comments claim they are the same shape — make them
  symmetric or fix the comment. `EnsureRowSaveAllowedAsync` bypasses the memoized `RowSecurity`
  service (`DefaultPersistentObjectActions.cs:302-307`), invoking hooks a second time per save,
  outside the documented once-per-(type,action) cost contract.

---

## M7 — One `DisableActions`, two visible tiers

**Decision 4: a real gate where it is cheap.**

Today there are three surfaces sharing no mechanism — `PersistentObject.DisableActions`,
`SparkQueryContext`/`CustomQueryArgs.DisableActions`, and `IClientAccessor.DisableActions*` — and the
third is **silently dropped on the detail path**, because `Get.cs:37` returns `Results.Json(obj)`
while only `ClientResult.Envelope` attaches client operations.

One mechanism, reaching every path, with two tiers:

- **Enforced** where the disabled set is re-derivable on execute without reloading the object —
  query-level withholds.
- **Advisory** where it needs the object; the action's own gate remains the boundary there.

**The tiers must be visible at the call site.** A uniform-looking call whose guarantee depends on
which surface it was made from is worse than either tier alone — that is the trap this decision
accepts, and naming is how it is paid for.

---

## M8 — An analyzer for the JSON

Build-time detection for what today loads cleanly and does nothing. Startup validation stays as the
backstop: `App_Data` is hand-edited and hot-reloaded (`SecurityConfigurationLoader.cs:116-144`) on
machines where no analyzer ever ran.

**`security.json`** — the validator today checks only the `a/b` resource shape and duplicate ids
(`SecurityConfigurationValidator.cs:36-58`). It does not check that the action name or the target
mean anything, so a typo grants nothing, silently:

- A right whose target names no known type, query or alias.
- An action name outside the built-ins, the combined table (`SparkCombinedActions.cs:16-28`) and the
  declared custom actions — `"Raed/Person"` is the canonical case.
- A three-segment resource, which parses but can never match (`Right.cs:21-27`).

**Model files** —

- A query alias in `persistentObject.queries` that cannot be parent-scoped (M2.3).
- A `Custom.MethodName` source naming a method that does not exist on the actions class — today a
  runtime reflection failure.
- A query with no `entityType` where one is required.
- A composed type whose attributes are all `showedOn: PersistentObject` (the zero-column cliff,
  currently caught only at load).

Next free diagnostic id after SPARK009. `docs/` already records that a `CodeFixProvider` needs its
own assembly.

---

## M9 — Reshape the actions surface

**Decision 8: properly.** Decision 2 makes the actions class the universal seam — every
PersistentObject can have one, entity-backed or not — so the member list should look like a designed
surface rather than an accreted one: everything on the interface, the `[NoInterfaceMember]` split
justified or removed, and the duck-typed lookups (`OnLoadAsync`, `OnQueryAsync`, `StreamItems`,
`RestrictToIds`) bound by something the compiler checks.

**This is the largest single source of diff in the plan** and it changes every consuming actions
class in the repository. It goes last of the code milestones for that reason: everything before it
is behaviour, and this is shape.

---

## M10 — The client inconsistencies

- **Reference links gate on `Query`, not `Read`** (`reference-link-route.pipe.ts:8` resolves against
  the `Query`-filtered catalogue) while the grid's first column correctly gates on `canRead`
  (`spark-query-grid.component.html:67`). Make them agree.
- **Reference pickers see 50 candidates, silently.** `executeQueryByName` forwards neither paging nor
  search (`spark.service.ts:76-82`), so every picker and AsDetail reference column truncates with no
  indication — and costs a full `GET /spark/queries` per reference attribute, un-deduplicated.
- **N+1 parent loads.** A detail page with N sub-query cards runs the parent's `OnLoadAsync` N+1
  times (`Execute.cs:126`). Cache per request or make cards lazy. Note this multiplies whatever M2
  adds to the load path.

---

## Verification

- Full framework suite, `CodeCoverage.Tests`, `ng-spark` — batched at the end, per repository policy.
- Both demo apps with sub-queries boot and render a parent detail page with populated cards.
- WebhooksDemo's github-projects page still works after M5 deletes the endpoint under it.
- `apps/CodeCoverage` boots with its M4 declarations.
- **Every startup refusal and analyzer diagnostic is asserted by a test, message text included.** A
  refusal whose message does not name the fix is a worse defect than the one it replaces.

## Sequencing

```
S1  S2                        (parallel, cheap)
 │   │
 M0 ─────────────────┐        (tests first — blocks M1, M5, M6, M9)
 │                   │
 M1  M2  M3  M4  M8  │        (independent of each other; M8 encodes M2.3's rule)
 │   │   │   │   │   │
 └───┴───┴───┴───┴─ M5 ── M6 ── M7 ── M9 ── M10
```

M1–M4 and M8 can land in any order once M0 is green. M5 needs them first because it moves the code
they fix; M9 goes last because it reshapes what everything else has just corrected.
