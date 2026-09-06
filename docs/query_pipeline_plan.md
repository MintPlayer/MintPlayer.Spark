# Plan — One read pipeline

Implementation plan for [`query_pipeline_PRD.md`](query_pipeline_PRD.md), whose **Decisions** table is
authoritative — this plan implements those answers and does not re-open them.

**Lands in its own PR, after #367 merges and deploys.** #367 carries the dead-queue fix; it is not
held behind a framework redesign. Related work: [`actions_and_coverage_plan.md`](actions_and_coverage_plan.md).

**Two rules that shape everything below.**

1. **M0 first, always.** Several milestones refactor enforcement code that has no tests, and the
   suites covering the most complex enforcement points install a permissive double. Refactoring
   under that is how a silent fail-open ships.
2. **No arm may return the source untouched.** Every branch of the read path either narrows, refuses,
   or records that narrowing was deliberately delegated. The prior art already routed sub-queries
   through the actions class and still leaked, because one arm of the routing did nothing — routing
   is necessary, not sufficient.

---

## Spikes

### S1 — Is a Raven-only LINQ row filter evaluable in memory? *(latent, with a named trigger)*

`RowSecurity.cs:594` compiles the same `LambdaExpression` it hands to RavenDB. Exactly one filter in
the repository uses Raven-only LINQ — `apps/CodeCoverage/.../CommitActions.cs:28`, `.In()` — and
`Commit` has no index binding, so the compiled path is unreachable for it **today**.

**The trigger is specific:** the day anyone adds `[GenerateIndex]` or an `indexName` to `Commit`, row
security switches to the compiled in-memory path and `.In()` is `Compile()`d for the first time.

**Method:** unit test, no server. Build the expression, compile, invoke against an instance. Three
outcomes: works, throws, or **silently returns `false`** — the last is the bad one, because it denies
rather than leaks, on a path chosen by the shape of the result rather than by the policy, and would
read as an indexing bug rather than a security one.

**Feeds:** if unsafe, M6 gains startup validation that compiles every declared filter once and
refuses a type whose filter is translation-only.

### S2 — What does an entity-less type's row-policy declaration say?

Decision 2 settled the model: the actions class is trusted and the framework records it. What remains
is the wording, and the CodeCoverage audit sharpened the requirement.

That audit came back **clean** — `MyAccountRow` is properly scoped. But the scoping lives **two layers
below the actions class**, in a service, as a single line of defence with no framework backstop. So
the declaration must **name where the scoping lives** — a pointer a reviewer can follow — not merely
assert that it exists. Write both declarations (`Home`, `MyAccountRow`) for real against that bar.

---

## M0 — Tests before refactoring *(blocks M1, M5, M6, M12)*

Nothing here changes behaviour. It makes everything after it safe.

1. **Replace the permissive doubles.** `PermissiveRowSecurity` is installed in five suites —
   `ExecuteCustomActionTests.cs:584`, `StreamingQueryExecutorUnitTests.cs:34`, `QueryExecutorUnitTests.cs:34`,
   `QueryExecutorRowShapeTests.cs:34`, `QueryExecutorIntegrationTests.cs:80`. **Deleting every
   enforcement call in those executors would leave all five green.** Add a `RecordingRowSecurity`
   (permissive, but records member, entity type, result type, action, row count) and a
   `DenyAllRowSecurity`. For each executor: assert `FilterAsync` *and* `RedactAsync` were each called
   once with the expected arguments, and that `DenyAllRowSecurity` puts zero rows on the wire.
2. **The RQL-shape test that does not exist.** `QueryExecutor.cs:578-582` records that RavenDB groups
   consecutive `Search` clauses and ANDs the group with its neighbours, so the security predicate
   must precede them — reversing it yields `OR`. **Nothing asserts the generated query shape**, so M5
   could silently invert it. Assert `(predicate) and (search or search)` before touching that code.
3. **Streaming row security, from zero.** Per-batch `FilterAsync`/`RedactAsync` on a *different
   session object* each batch (nothing currently pins the fresh-session-per-batch fix);
   `ResetRequestFilterCache` fires on the re-auth tick; a revoked right ends the stream.
4. **The breadcrumb gap** from the facets pass: `BreadcrumbResolverTests` stubs `IRowSecurity` with
   `ReturnsForAnyArgs(true)`, so it proves the plumbing and never that a real filter reaches the
   decision. Assert a referenced row the filter hides renders as the redacted placeholder. This
   surface has already had one real bypass (untyped load, fixed preview.71).
5. **Characterise the list pipelines** as they behave today, including `GET /spark/po/{type}` having
   no cap — the before-picture for M5.
6. **Sort-after-redaction and the destructive `DistinctBy`.** A redacted column used as
   `?sortColumns=` must not order by the hidden value; an in-memory custom query must keep its
   null-id rows.

---

## M1 — Close F1, F2, F3

Three fail-opens, each untested. Each gets a test that fails before the fix.

- **F1 — `TotalItems` bypasses row filtering** (`QueryExecutor.cs:94-107` vs `:871`). Decision 9:
  **make the count honest.** Costs author-paging its cheap count; row security was never
  transferable, and the code says so at `:755-758`.
- **F2 — the custom-action row gate skips on unresolvable `clrType`** (`ExecuteCustomAction.cs:270`).
  Split the condition: a composed type (no `clrType` *declared*) proceeds; a declared-but-unresolvable
  one **throws**, matching every other path.
- **F3 — `ActionsResolver` degrades silently** (`:47-62`). A `FooActions` that is not
  `IPersistentObjectActions<Foo>` becomes a loud startup error, and `FindActionsType`
  (`:108-118`) refuses on ambiguity instead of caching an arbitrary first match across assemblies.

---

## M2 — The parent reaches every branch, and every arm narrows

Decision 3, plus the binding requirement the prior art produced.

1. **Give `ExecuteDatabaseQueryAsync` the parent.** Absent from the signature entirely (`:473`) while
   the custom branch receives it three lines earlier.
2. **A query used as a sub-query routes through the actions class**, so the author scopes it with the
   idiom that already works (`apps/DemoApp/.../CarActions.cs:34-38`).
3. **Audit every arm of the source dispatch for a silent pass-through.** This is the prior art's
   defect: an unrecognised source type returned unfiltered, while the sibling sort hook threw on the
   same input. Any Spark arm that returns a source untouched must instead narrow, refuse, or record
   delegation (M4).
4. **Refuse an un-parent-scopable sub-query alias** — `--spark-verify-model` at CI time (M10),
   startup validation as the backstop. The message names the parent type, the alias and the fix.
5. **Wire or delete `OnLoadAsync`'s `parent`.** All three call sites pass `null`
   (`DatabaseAccess.cs:99`, `:143`, `:534`); `Execute.cs:126` is the natural supplier. If nothing
   needs it once M3 lands, **delete the parameter** rather than shipping a documented lie.

---

## M3 — `OnQueryAsync`, reshaped

Decision 5: no reason enum — the hook receives the query and a `PersistentObject? parent`;
`parent != null` is a detail tab. **Validated by a negative result**: across 36 prior-art
applications, references to their reason enum's detail-query value number **zero**, and their exports
relabel the reason anyway. Threading intent into a filter hook buys nothing and any real use would be
a weakening branch.

Recorded limit: a standalone query page and a custom-action selection re-run both arrive with
`parent == null` and are indistinguishable. Fine for withholding actions; the hook can never be where
export-versus-list behaviour is decided.

Plus both defects in the hook as it stands — ours, from the preceding work:

- **Put it on the interface.** It is neither on `IPersistentObjectActions<T>` nor marked
  `[NoInterfaceMember]`, unlike four other off-interface members. (Note: that attribute is **inert** —
  `GenerateAutoInterface` appears nowhere in the solution — so it documents nothing either.)
- **Fire it for queries with no `entityType`** (`:191-193` short-circuits).

---

## M4 — Row policy is declared, and says where it lives

Decision 7: refuse, loud and early.

- **A type granted to a well-known group must declare a row policy**, even if allow-all. Startup
  refuses otherwise.
- **An entity-less type declares that its actions class owns row security**, via an interface
  carrying a **non-empty rationale string** — so the declaration is a sentence someone wrote, checked
  at boot and rendered at startup, rather than an inference from `entityType is null`. Per S2 the
  rationale must name *where* the scoping lives; `MyAccountRow`'s is two layers down in a service.
- **`DatabaseAccess.LoadVirtualObjectViaActionsAsync` (`:505`) is covered by the same rule.** It
  returns rows with zero row-security calls and, unlike the composed query, no announcement at all.
- Validate in `SparkComposedQueries.Validate`, which already runs at load *and* under
  `--spark-verify-model`, so an undeclared type is a CI failure rather than a first-request surprise.

`apps/CodeCoverage` needs declarations for `Home` and `MyAccountRow`. Confirming the declaration it
needs is the one it wants is work in this milestone, not a rubber stamp.

---

## M5 — One sealed pipeline

The refactor. Requires M0.

**Mechanism: a `SecuredRows` token.** A `sealed class` (not a struct — `default(struct)` would be a
forged token) with a private constructor, nested in the gate, so only the gate can produce one. Flip
`QuerySourceResult`, `QueryResultProjector.ToItems` and the streaming batch to demand one, and
deleting an enforcement call stops compiling instead of drawing a review comment.

`RowSecurityMode` travels on the token: `Enforced` or `DelegatedToActions`, derived — never passed by
the caller — and **throwing when neither applies**. Forgotten and delegated stop being the same
observable.

- **Delete `GET /spark/po/{objectTypeId}`** (decision 6) with `GetPersistentObjectsAsync` and
  `SparkService.list`. Single caller — `apps/WebhooksDemo/.../github-projects.component.ts:63` —
  moves to `/execute`. Touches `DenyAllEndpointMirrorTests` and two `DatabaseAccess` suites.
- **Streaming joins the stage**, keeping its per-batch session and its re-auth tick (which must keep
  the memo reset coupled to the right re-check, and must carry forward the documented residual: a
  frozen handshake principal misses user-level revocation).
- **Resolve the two-`Query`-authorization divergence** (`:485` vs `:528`), documented but unfixed at
  `SubQueryPruner.cs:81-85`.

**Ordering constraints that are real and must survive**: pushdown before the search clause (RQL
`AND`/`OR`, guarded by M0.2), and in-memory sort *after* redaction (ordering by a hidden value is a
comparison oracle). Pass the ordering into the stage so it is structural rather than a comment.

Migration order: `GetPersistentObjectsAsync` → database branch → custom branch → streaming →
`LoadManyAsync` → seal. Sealing last means the build is the verification.

Not migrated, deliberately: the write-side gates and `AreAllowedAsync`, which is a refinement over
already-loaded ids rather than an entry point. A sealed *write* stage is a coherent follow-up, not
this design.

---

## M6 — Row-security invariants, stated and enforced

- **Assert the one-element-source invariant**: "can this user see this row" *is* "does this row
  survive the list filter". `ResolveEffectiveRuleAsync` (`RowSecurity.cs:512-545`) already implements
  it; make it tested rather than emergent.
- **No silent pass-through on an unclassifiable element type** — the prior art's defect, restated as
  a Spark invariant. Spark is currently on the right side: `ComposeRowFilterAsync` no-ops on a
  projection but `FilterAsync`'s base-document reload is the real gate, so the no-op is *backed*
  rather than final. Audit every arm for that property and test it.
- **Startup filter validation** if S1 says compiled evaluation is unsafe.
- **Symmetric write gates.** Update uses a side session (`DatabaseAccess.cs:278`), delete the shared
  request session (`:341`), while the comments claim they are the same shape. `EnsureRowSaveAllowedAsync`
  bypasses the memoized service, invoking hooks a second time per save outside the documented
  once-per-(type,action) contract.

---

## M7 — One `DisableActions`, two visible tiers

Decision 4. Three surfaces today share no mechanism, and `IClientAccessor.DisableActions*` is
**silently dropped on the detail path** because `Get.cs:37` returns `Results.Json(obj)` while only
`ClientResult.Envelope` attaches client operations.

One mechanism, every path, two tiers: **enforced** where the disabled set is re-derivable on execute
without reloading the object, **advisory** where it needs the object. **The tier must be visible at
the call site** — a uniform-looking call with a surface-dependent guarantee is worse than either tier
alone, and that is the cost this decision accepts.

---

## M8 — Wire the JSON in, once *(blocks M9)*

Decision 11. Today model JSON is hand-wired in five csprojs, `security.json` in none, and neither
appears in any `.targets` — so external consumers get nothing **and the failure is silent**.

- **`spark.props`** (imported first): defaults only — `$(SparkAppDataDir)`,
  `$(SparkIncludeAppDataAdditionalFiles)`.
- **`spark.targets`** (imported last, so a consumer's override wins): the `AdditionalFiles` items.
- **`Condition="Exists(...)"` on every named file** — an item naming a missing file fails the build
  (`docs/guide-queries-and-sorting.md:491`). Globs are safe.
- **Delete the five apps' hand-written lines in the same change**, or every app gets duplicate items.
- Include: `Model/*.json`, `translations.json`, `culture.json`, **`security.json`**,
  **`customActions.json`** (needed to validate that a right names a real action),
  **`programUnits.json`**. Exclude **`modelHashes.json`** — decision 12.

Precedent: `MintPlayer.Spark.Authorization` already packs both a `.props` and a `.targets`.

---

## M9 — SPARK011: an analyzer for `security.json`

Decision 10. The file is hand-authored and never machine-written, so there is no staleness window.
`RegisterCompilationStartAction` gives both `AdditionalFiles` and the symbol table;
`DefaultIndexAnalyzer` already uses that shape.

Checks, all currently missing (the validator today verifies only the `a/b` resource shape and
duplicate ids):

- a target naming no known type, query or alias;
- an action name outside the built-ins, the combined table and the declared custom actions —
  `"Raed/Person"` is the canonical case, and it loads cleanly today granting nothing;
- a right whose `groupId` is not in `groups`;
- a three-segment resource, which parses but can never match.

**Cost to price honestly:** the analyzer project is netstandard2.0 with **no JSON parser** —
`ModelJsonReader` is deliberately a regex reader. Anchoring a diagnostic to a line needs real byte
offsets, so this ships `System.Text.Json` inside `analyzers/dotnet/cs`, the classic assembly-load
conflict surface. This is the largest single cost in the milestone and it is not small.

Also fix the two docs that cite analyzers which do not exist: `README.md:355` (SPARK003) and
`MintPlayer.Spark.Controllers.csproj:15` (claims to ship SPARK010, packs no analyzer).

---

## M10 — Extend `--spark-verify-model` instead of analyzing model JSON

Decision 10's other half. Model synchronisation runs *after* compilation in a separate process, so an
analyzer over model JSON is permanently one build behind — **already evaluated and rejected in this
repository** (`docs/issue_272_273_275_276_PRD.md:314-319`). `--spark-verify-model` runs after
synchronise and is never stale.

- **`Custom.MethodName` resolves to a real method** on the actions class. Today it is reflected at
  execution time, so a typo is a runtime 500 on first page view.
- **`clrType`-less model files are validated structurally** — currently the single largest CI hole:
  editing a label in `MyAccountRow.json` and re-running the gate exits 0.
- **The M2.4 sub-query rule** — an alias that cannot be parent-scoped.
- **Promote `ModelLoader.cs:87-96`**, which `Console.WriteLine`s an unparseable model file, skips it,
  and starts the app with that entity type silently missing.

---

## M11 — Close the model-hash gaps

The runtime gate is sound: it fails closed on a missing *or corrupt* file, has no boolean off-switch,
and production runs as `Production` with no override set. Three gaps:

- **The deploy workflow verifies nothing.** `--spark-verify-model` runs only in `pull-request.yml:136`;
  `code-coverage-deploy.yml` goes checkout → build → push → deploy. Either add it to the deploy path
  or record an explicit decision that pre-merge plus branch protection is sufficient — the mitigating
  fact being that `COPY . .` makes model and binaries same-commit by construction.
- **`customActions.json` and `programUnits.json` have no integrity gate at all.** The hash glob is
  `App_Data/Model/*.json`; `security.json` has a separate mechanism. These two have nothing.
- **Success is silent** (`ModelHashVerifier.cs:58-59`), so "gate passed" and "gate never ran" are
  indistinguishable from outside — in a deployment that has already had a subsystem silently dead in
  production. One startup line naming the number of hashes verified fixes it.

---

## M12 — Reshape the actions surface

Decision 8. The shape falls out of signatures that are **already `T`-free** — `OnLoadAsync`,
`OnQueryAsync`, `GetDefaultIncludes` and `RestrictToIds` mention no `T`, which is exactly why the
composed duck-typed path can demand identical signatures today.

- **`ISparkActions`** (non-generic) for everything an entity-less type can do; **`ISparkActions<T>`**
  for the members that genuinely mention `T` (`GetRowFilterAsync` must stay generic — the
  `Expression<Func<T,bool>>` is what makes pushdown possible); an abstract base so a composed author
  writes `override` and the compiler catches the signature instead of a first-request throw.
- **Renames that stop the surface lying**: `IsAllowedAsync` → `IsRowAllowedAsync` (it collides by name
  *and* action vocabulary with the **type-level** `IPermissionService.IsAllowedAsync`);
  `GetProtectedAttributesAsync` → `GetRedactedAttributesAsync`; `OnQueryAsync` →
  `OnQueryConstructAsync` (its current doc says it shapes a *result*; it runs before execution and
  can never see one).
- **Delete `StreamItems`/`StreamItem` from the base** — resolved by model-declared name, never by
  these, so they imply a hook that does not exist. `MaterializeAsync` becomes `protected`.
- **`SparkRefreshArgs<T>`'s type parameter is decorative** — no member is typed `T`. Making it
  non-generic gives composed types refresh, which they cannot have today.
- **Register composed actions classes** — the generator matches only `DefaultPersistentObjectActions<T>`,
  so `ActionsResolver` constructs a fresh instance per resolution.
- **A generator can absorb the open-set members**: custom query and streaming methods are named in the
  model, so the author keeps writing a plain method and the generator emits the interface half,
  replacing four reflective shape-validators with compiler checks.

Blast radius: **52 entity-backed classes** (base-type rename), **~13 composed** (gain a base +
`override`), **1 hand-written implementer**, ~32 override-site renames, **12 framework files** where
the real work is. No application *logic* changes. Largest diff in the plan, which is why it is last:
everything before it is behaviour, this is shape.

---

## M13 — The client inconsistencies

- **Reference links gate on `Query`, not `Read`** (`reference-link-route.pipe.ts:8`) while the grid's
  first column gates on `canRead` (`spark-query-grid.component.html:67`).
- **Reference pickers see 50 candidates, silently** — `executeQueryByName` forwards neither paging nor
  search, and costs a full `GET /spark/queries` per reference attribute, un-deduplicated.
- **N+1 parent loads**: a detail page with N sub-query cards runs the parent's `OnLoadAsync` N+1 times
  (`Execute.cs:126`). Multiplies whatever M2 adds to the load path.

---

## Verification

- Full framework suite, `CodeCoverage.Tests`, `ng-spark` — batched at the end, per repository policy.
- Both demo apps with sub-queries render a parent detail page with populated cards.
- WebhooksDemo's github-projects page works after M5 deletes the endpoint under it.
- `apps/CodeCoverage` boots with its M4 declarations, and the five apps build after M8 removes their
  hand-written `AdditionalFiles`.
- **Every startup refusal and analyzer diagnostic is asserted by a test, message text included.** A
  refusal whose message does not name the fix is a worse defect than the one it replaces.

## Sequencing

```
S1  S2                                  (parallel, cheap)
 │   │
 M0 ────────────────────┐               (tests first — blocks M1, M5, M6, M12)
 │                      │
 M1  M2  M3  M4         │               (independent of each other)
 │   │   │   │          │
 │   │   │   │   M8 ── M9               (wiring, then the analyzer)
 │   │   │   │   M10  M11               (CI + gate gaps, independent)
 └───┴───┴───┴────┴─── M5 ── M6 ── M7 ── M12 ── M13
```

M1–M4, M8–M11 can land in any order once M0 is green. M5 needs them first because it moves the code
they fix; M12 goes last because it reshapes what everything else has just corrected.
