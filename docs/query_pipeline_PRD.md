# PRD — One read pipeline: queries, items, sub-queries and row security

**Status:** design agreed, not implemented. Written 2026-09-06.
**Lands in:** its own pull request, *after* PR #367 merges and deploys. The one-PR rule is held
for related fixes; an outage fix already awaiting deploy is not held behind a framework redesign.
Owner decision, 2026-09-06.
**Backward compatibility:** explicitly waived by the owner. Breaking changes to the actions surface are in scope.

---

## Why this exists

Four things are supposed to cooperate and currently do not:

1. **Opening a query-list page** — "Cars overview".
2. **Opening one item** from that list.
3. **Row filtering**, which must apply to both, and to writes.
4. **Sub-queries** — a detail page embedding a query of a *different* type, which may be a type with no CLR entity at all.

Each works in isolation. The seams between them are where the defects live, and five parallel investigations found the same shape of problem at every seam: **the framework has one rule and many application points, and an application point that forgets is silent.**

---

## What we found

Every claim below carries a file:line and was verified in source. Three load-bearing ones were re-verified by hand after the investigation, because they are the ones the design rests on.

### The headline defect: a `Database.*` sub-query lists the entire collection

`ExecuteQueryAsync` dispatches on the source prefix (`QueryExecutor.cs:71-85`):

```csharp
source = await ExecuteCustomQueryAsync(query, name, parent, searchTerm, …);   // :71  — parent
source = await ExecuteDatabaseQueryAsync(query, name,         searchTerm, …); // :84  — no parent
```

`ExecuteDatabaseQueryAsync` (`:473`) has **no `parent` parameter at all**. The client sends `parentId`/`parentType` faithfully (`spark.service.ts:65-66`); the endpoint resolves and authorizes the parent (`Execute.cs:118-133`); and then the database branch discards it.

So a `Database.*` query declared in a parent's `persistentObject.queries` renders **every row of the child collection** under that parent's detail page. There is no error, no warning, and nothing at startup refuses the configuration. It has not been hit because every sub-query in the demo models happens to be `Custom.*`.

**Severity note.** This is a *scoping* failure, not an authorization failure — row security still runs, and the child type's `Query` right is still enforced three times over. A user sees rows they are permitted to see, under a parent they have nothing to do with. It is wrong and confusing rather than a breach, and it is a breach only in the composed case below.

### The parent context dies twice more

- **`OnLoadAsync(string id, PersistentObject? parent)`'s `parent` is dead.** The interface documents it as "the object this load is nested under" (`IPersistentObjectActions.cs:22-23`). All three framework call sites pass literal `null` — `DatabaseAccess.cs:99`, `:143`, `:534` — verified by reading the `Invoke` argument arrays. No code path supplies a non-null parent.
- **Streaming has no parent at all.** `StreamingQueryArgs` carries no `Parent`, and `StreamExecuteQuery` never reads the parameters.

### Entity-less types are outside row security entirely

A model type with no `clrType` is *composed*. `SparkComposedQueries.IsComposed` (`:23`). Both enforcement calls are guarded on the entity type being resolvable (`QueryExecutor.cs:869-872`, `:883-884`):

```csharp
var entityList = entityType is not null
    ? await rowSecurity.FilterAsync(…)
    : rawRows;
```

This is deliberate and documented in place: redaction compares a mapped attribute against the stored document, and a composed row has none. The type-level `Query` right is still enforced (`:674`), a composed row carries no `can` block by construction, and every composed query announces itself at startup.

**But it means items (3) and (4) of the brief do not cooperate today.** A sub-query over a composed type gets no row filtering, by construction — and it is precisely the composed case where the parent-drop above becomes a real disclosure, because nothing downstream re-narrows the result. Production uses composed types (`apps/CodeCoverage/.../Home.json`, `MyAccountRow.json`).

### There are four list pipelines, not one

| Path | Row filter | `OnQueryAsync` | Paging / search / sort | `take` cap |
|---|---|---|---|---|
| `GET /spark/queries/{id}/execute` | yes | yes | yes | 1000 (`Execute.cs:105-115`) |
| `GET /spark/queries/{id}/stream` | own copy (`StreamingQueryExecutor.cs:145`) | **never** | n/a | n/a |
| `GET /spark/po/{objectTypeId}` | yes (`DatabaseAccess.cs:191`) | **never** | **none** | **none** |
| custom-action selection fallback | per-id load | **never** | n/a | n/a |

Row *security* is applied on three of the four — the enforcement is in better shape than the plumbing. What diverges is everything else, and the third row is reachable and unbounded while `/execute` clamps `take` for exactly that reason.

### Fail-open points

| ID | Finding | Location |
|---|---|---|
| **F1** | `TotalItems` for an author-paged query is returned verbatim while `FilterAsync` removes rows afterwards. The rows are filtered; **the count is not**. A cardinality oracle on a row-scoped type. The code asserts three lines away that row security is not transferable to the author. | `QueryExecutor.cs:94-107` vs `:871` |
| **F2** | The custom-action row gate is guarded on `clrType is not null`, so an unresolvable CLR type **skips `AreAllowedAsync` and proceeds**. Intentional for composed types; it also swallows a renamed class or unreferenced assembly, which every other path throws loudly on. | `ExecuteCustomAction.cs:270` |
| **F3** | If `FooActions` exists but is not `IPersistentObjectActions<Foo>`, the cast fails and control falls through to the permissive default — no diagnostic, `HasRowRule` false, type unrestricted. `FindActionsType` resolves by **simple name across every loaded assembly, first match wins**, permanently cached. | `ActionsResolver.cs:47-62`, `:108-118` |
| **F4** | "No rule ⇒ unrestricted" is deliberate and documented, and safe only while the type-level grant holds. Granting `anonymous` makes the row filter the sole gate between the public internet and the collection — which production relies on. | `RowSecurity.cs:524-525`; `guide-row-security.md:98-119` |
| **F5** | One `LambdaExpression` is used both for RavenDB translation and for `filter.Compile()` in-memory evaluation. Nothing validates that a filter written with Raven-only LINQ is evaluable in memory. Hazard class, not a confirmed defect. | `RowSecurity.cs:594` |

**F1, F2 and F3 have no test coverage.** `PermissiveRowSecurity` is installed in both the custom-action and streaming suites (`ExecuteCustomActionTests.cs:584`, `StreamingQueryExecutorUnitTests.cs:34`), leaving the two most complex enforcement points unexercised. F2 lives exactly there.

### Three `DisableActions` surfaces that share no mechanism

`PersistentObject.DisableActions` (serialized on the PO), `SparkQueryContext`/`CustomQueryArgs.DisableActions` (`QueryResult.DisabledActions`), and `IClientAccessor.DisableActions*` (envelope-only). The third is **silently dropped on the detail path**, because `Get.cs:37` returns `Results.Json(obj)` while only `ClientResult.Envelope` attaches client operations. The three look interchangeable and are not.

### Smaller, but in scope

- **`OnQueryAsync` is not on `IPersistentObjectActions<T>`** and is not marked `[NoInterfaceMember]` either, unlike the four other off-interface members — invisible through the interface-typed resolver overload. (Introduced earlier in this same work; a defect of ours.)
- **`OnQueryAsync` never fires for a query with no `entityType`** (`QueryExecutor.cs:191-193`), though such queries are supported. Silent hook omission is the exact failure the hook's own documentation warns about.
- **N+1 parent loads.** A detail page with N sub-query cards runs the parent's `OnLoadAsync` N+1 times (`Execute.cs:126`). No dedup, cards are not lazy.
- **Two `Query` authorizations on different names** on the database path — `query.EntityType` (`:485`) then the CLR-resolved definition name (`:528`); the effective grant is their intersection. `SubQueryPruner.cs:81-85` documents the divergence rather than fixing it.
- **Reference links are gated on `Query`, not `Read`.** `reference-link-route.pipe.ts:8` resolves against the `Query`-filtered catalogue, while the grid's first column correctly gates on `canRead`. A clickable link that refuses on arrival. Cosmetic; the refusal is a constant-body 404.
- **Reference *ids* survive redaction.** Only the label is placeholder'd (`EntityMapper.cs:247`).
- **Reference pickers see at most 50 candidates**, silently — `executeQueryByName` forwards neither paging nor search (`spark.service.ts:76-82`), and costs a full `GET /spark/queries` per reference attribute, un-deduplicated.
- **Write gates are asymmetric** in a way the comments deny: update checks the pre-image in a dedicated side session (`DatabaseAccess.cs:278`), delete on the shared request session (`:341`); and `EnsureRowSaveAllowedAsync` bypasses the memoized `RowSecurity` service, calling hooks directly — a second invocation per save, outside the documented once-per-(type,action) contract.

---

## What the prior art tells us

A mature framework in the same problem space was reviewed. It is a third party's private source; everything here is described generically and no source was copied.

**Its architecture validates Spark's central choice and demonstrates the cost of the alternative.** It splits the read projection type from the write aggregate type and gives row filtering *two* hooks — one per shape — bridged by a conditional whose false branch returns the source unfiltered. The list is filtered; the single row is not. On its mainstream storage provider the bridge is severed outright, because the provider overrides the load path and drops the security call.

The empirical result, counted across its consuming applications on this machine:

| | Files |
|---|---|
| Override the list filter | **289** |
| Of those, also guard the single-row path | **30 (10%)** |
| Narrowing to plausibly security-motivated list filters | **194** |
| Of those, also guarding the single-row path | **30 (15%)** |
| Override the hook that *bridges* the two | **0** |

Six in seven principal-aware list filters have no matching single-row guard, and in tens of thousands of files nobody has ever overridden the bridge. That is not a discipline problem; it is a 15% adoption rate on the second half of a contract the framework requires but never checks. Its own practitioner documentation reads as a list of workarounds — restate the rule in three or four places, and nothing verifies they agree.

**Spark's single `GetRowFilterAsync`, composed once and applied to every path, avoids this by construction.** `ResolveEffectiveRuleAsync` (`RowSecurity.cs:512-545`) ANDs the filter with the per-row hook, and the compiled filter is non-null whenever the filter is — so a filter-only type *is* gated on single-row reads, and list and detail cannot diverge. That is worth stating as an invariant rather than leaving as an implementation detail.

Other things worth copying: its **separate `Query` and `Read` rights** with click-through re-entering the full object pipeline (Spark already has this); its **reason enum** threaded into query hooks so one hook serving list/count/export/search makes the caller's intent visible; and its **sub-query design**, which is genuinely good — the *child's* hooks fire, the parent object (not just its id) is available, and the child's own filter is applied on top. Spark already resolves the child's actions class correctly; what it lacks is the parent.

Things to avoid, each of which Spark is currently at risk of: a row filter whose default is the identity function (indistinguishable from a forgotten override); a `default:` arm in a type dispatch that passes unclassifiable sources through unfiltered; row security applied at a point a storage adapter can forget to call; no WITH CHECK on insert (Spark has one — keep it); and confusing UI pruning with authorization.

---

## Decisions

Nine questions were put to the owner before any implementation. The answers below are settled; they
are recorded here so the plan does not re-litigate them.

| # | Question | Decision |
|---|---|---|
| 1 | Does this land in PR #367? | **No.** #367 ships and deploys on its own — it carries the dead-queue fix. This gets its own PR. |
| 2 | Row security for entity-less types? | **The actions class is trusted, and the framework says so.** Every PersistentObject can have an actions class, entity-backed or not; that class is the universal seam. |
| 3 | How does a sub-query get parent-scoped? | **Pass the parent to the `Database.*` branch and require an actions method.** No relation language in the model. |
| 4 | Should `DisableActions` become a real gate? | **Where it is cheap.** Enforced on execute where the disabled set is re-derivable without a reload; cosmetic where it needs the object. |
| 5 | How does the query hook signal intent? | **No reason enum.** `OnQueryAsync` receives the query and a `PersistentObject? parent`; `parent != null` means a detail tab, `null` means a query page. |
| 6 | What happens to `GET /spark/po/{type}`? | **Deleted**, and its one caller (WebhooksDemo) moves to `/execute`. |
| 7 | How strict are the new startup refusals? | **Refuse both, loud and early** — an un-parent-scopable sub-query and an undeclared row policy on a well-known-group type. |
| 8 | How far does "no backward compatibility" go? | **Reshape the actions surface properly.** It is the universal seam per decision 2, so the member list should look like it. |
| 9 | How is F1 (the count leak) fixed? | **Make the count honest** — count after filtering rather than trusting the author's total. |

Two consequences worth stating up front, because they are the ones that will bite:

- **Decision 4 buys a non-uniform guarantee.** An author calling `DisableActions` gets enforcement on
  one surface and an affordance on another. The API must make which one visible at the call site;
  a uniform-looking call with a surface-dependent guarantee is worse than either tier alone.
- **Decision 5 cannot distinguish a standalone query page from a custom-action selection re-run** —
  both arrive with `parent == null`. That is fine for withholding actions, which is what the hook is
  for, but it means the hook can never be the place where export-versus-list behaviour is decided.

---

## Goals

1. **One read pipeline.** Every path that returns rows — list, sub-query, detail, streaming,
   custom-action selection — passes through one sealed enforcement stage. Enforcement must not be a
   virtual method a caller can forget. `GET /spark/po/{type}` is deleted rather than folded in.
2. **The parent reaches every branch.** `ExecuteDatabaseQueryAsync` receives the parent it is
   already handed, and a query used as a sub-query routes through the actions class so the author
   scopes it. No relation language.
3. **A sub-query that cannot be parent-scoped is refused** — by an analyzer at build time, and by
   startup validation as the backstop.
4. **The actions class is the universal seam.** Every PersistentObject can have one, entity-backed
   or not, and for an entity-less type it owns row security outright — recorded at startup, not
   silently assumed.
5. **A type granted to a well-known group must declare a row policy**, even if that declaration is
   allow-all. Refused otherwise.
6. **The hook knows where it is.** `OnQueryAsync` receives the query and a nullable parent; that is
   the whole intent signal.
7. **One `DisableActions` mechanism**, reaching every path including detail, with its two
   enforcement tiers visible at the call site.
8. **Misconfiguration is a build error, not a runtime surprise.** An analyzer over the model and
   `security.json` catches what today loads cleanly and does nothing.
9. **F1–F3 closed, with tests**, and `PermissiveRowSecurity` removed from the two suites that hide them.

## Non-goals

- **Splitting the row filter per shape.** The prior art proves where that ends. One declared predicate, composed by the framework.
- **A projection/aggregate type split.** Not being introduced. If it ever is, the binding must be declared and total, and an untranslatable filter must deny.
- **Removing the F4 fail-open wholesale.** "No rule ⇒ unrestricted" stays for types whose type-level
  grant is itself a real constraint. It is tightened only where the grant is a well-known group.
- **Making `DisableActions` an authorization boundary everywhere.** It becomes one only where the
  decision is re-derivable cheaply; elsewhere it stays an affordance and the action's own gate is
  the boundary.
- **A relation language in the model.** Considered and rejected — the existing `args.Parent!.Id`
  idiom already works, and inventing model vocabulary to avoid one `.Where` is a poor trade.
- Facets, distinct values and search suggestions — they do not exist yet. When added they inherit the pipeline by construction, which is the point of goal 1.

## Risks

- **The sealed pipeline stage is a large refactor** touching four call paths, one of which (streaming) has no row-security tests at all. The tests come first — see plan M0.
- **Startup refusal for un-parent-scopable sub-queries is a breaking change** for any app with such a configuration. Nothing in this repository has one, but a downstream consumer might; the failure is loud and the message names the fix.
- **Requiring an explicit row policy on well-known-group types** will fail production apps at
  startup until they declare one. Deliberate — that is the population where a forgotten filter is a
  public disclosure. `apps/CodeCoverage` will need a declaration, and confirming the declaration it
  needs is the one it actually wants is work, not a rubber stamp.
- **Reshaping the actions surface (decision 8) touches every consuming actions class** in the
  repository and every downstream one. Preview is when that is cheapest, but it is the largest
  single source of diff in this plan.
- **F5 cannot be resolved without measurement.** Whether Raven-only LINQ is evaluable in memory needs a spike, not a decision.

## Out of scope

Genuinely not being done, not deferred to avoid a large diff:

- Facets / distinct values / search suggestions (do not exist).
- The `SparkReplicationExtensions.cs:87` `GetEntryAssembly()!` trap — known, deliberately left.
- Reference-id exposure under redaction (F6) — the id lives on a document the caller may read; low value, and changing it churns the wire shape.
