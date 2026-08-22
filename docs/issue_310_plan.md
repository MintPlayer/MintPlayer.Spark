# Plan — `security.json` moves into Spark core

**PRD:** `docs/issue_310_PRD.md`
**Issue:** [#310](https://github.com/MintPlayer/MintPlayer.Spark/issues/310)
**Branch:** `feat/security-json-in-core`
**Base:** `master` @ `7ad2e30`
**Release:** `10.0.0-preview.62`

---

## Milestones

| M | Title | Breaking? |
|---|---|---|
| M0 | Spikes | — |
| M1 | Cherry-pick the non-authorization fixes from #308 | no |
| M2 | Move the models and the loader to core | namespaces |
| M3 | Move the evaluator, validator and claims provider to core | namespaces |
| M4 | `AddSpark` registers it all; `AddAuthorization()` **deleted** | **yes** |
| M5 | Startup gate + `--spark-synchronize-security` generator | **yes** |
| M6 | Delete `AllowAnonymousAccess()` and fix the `SparkDenial` predicate | **yes** |
| M7 | Test infrastructure: default file + per-test override | **yes** |
| M8 | Symmetric deny expansion | behaviour |
| M9 | Request-scoped permission memo, then prune sub-queries by `Query` right | behaviour |
| M10 | Demo `security.json` files, all four | **yes** |
| M11 | Wire `--spark-verify-security` + commit baselines | no |
| M12 | Docs: package README, guides, the three false doc statements | no |
| M13 | Version, release notes | — |

M2–M4 are one logical move split for reviewability of the diff, not for separate landing —
they must all be in before anything compiles. M6 depends on M4. M7 depends on M5 and M6.

**Explicitly NOT here:** the `spark-sub-query` chrome work (#309). It continues on
`fix/parentless-sub-query` minus `rowsNavigable`, and rebases onto this once it lands. Its
header-slot redesign uses the `*bsDatatableColumn`-style structural directive pattern, not the
`<ng-content>` or `TemplateRef` shapes the earlier draft proposed.

**No backward compatibility.** Owner directive: no shims, no `[Obsolete]`, no compatibility
overloads, no forwarding types. Deleted means deleted. That does not mean nothing must keep
compiling — `SparkFullGenerator.Producer.cs:102-103` emits a literal `AddAuthorization(...)`
call, so every `AddSparkFull` app breaks the moment M4 lands unless the generator changes in
the same commit.

---

## M1 — Cherry-pick from #308

Owner's decision: PR #308 is not merged; the non-authorization fixes come across, and #308
closes unmerged. Bring:

- the three-state template restructure (invisible spinner, zero-DOM failure, stale chrome on
  reload)
- the `spark-query-list` unguarded `async` subscribe — a denied query renders a **permanent
  spinner** today
- `[indeterminate]`, the permission-state reset, the fetch-failure surface
- `reload()` / `reloadToken`, the `refreshQuery` client handler
- the `showedOn: 'query'` filter fix
- the `[object Object]` fixes, the `Execute.cs` `SparkQuery` clone fix
- **the M-3 sweep** (`SparkDenial`, the `Queries/Execute.cs` sort-column ordering fix,
  `ExecuteCustomAction`, `ListCustomActions`, `GetPermissions`)
- the selection-rule parsers, enforcement, ceiling, and the row-action gate
- the grid core

Do **not** bring: `rowsNavigable`, `SparkQuery.Actions`, and — pending the #309 redesign —
`headerRenderer`/`headerRendererOptions`, which the sub-query consumes and the query-list does
not.

⚠️ The M-3 work arrives here, so M6's `SparkDenial` predicate change is editing code that
lands in this same PR. Keep M1 and M6 as separate commits so the predicate change is legible.

---

## Spikes

Run all of M0 before M2. Each can come back "no".

### ~~S1~~ — answered: yes it does, and it already does today

**244** factory-booted tests, not 163 — `IdentityProvider/OidcTestHost.cs:41` is one host that
11 subclasses inherit (+144). Exactly 10 override the security layer.

Delete `EnsureAuthorizedAsync("Read", ...)` from `DatabaseAccess.cs:84` and every one of them
still passes — **today**, because `SparkEndpointFactory.cs:99` already calls
`AllowAnonymousAccess()`. The permissive default is neutral, not a new weakness. The answer is
not to make 244 tests state a rights model; it is the deny-all mirror suite in M7.

Remaining spike, narrower: **do any of the 144 OIDC tests route through `IPermissionService`?**
They are minimal-API endpoints on the module's own routes, so probably not — but if any does,
the default moves 144 tests at once.

### S8 — Does the wildcard compose with everything it touches?

PRD F18 adds `*/*` and `Action/*` to a matcher that is exact string equality today.

**Method:** unit-test the matcher against bundle expansion, denial-first precedence, and the
posture reporter. Assert a `*/*` grant plus a `Delete/Car` denial denies delete.
**Pass:** all three compose, and the posture report prints `*/*` verbatim.
**Fail:** the wildcard needs its own precedence tier, a bigger change than F18 assumes.

### ~~S2~~ — answered: the predicate is not a configuration question

*Was: is "does anonymous hold any grant" the right predicate for 401-vs-404?* **No — it would
have shipped a silent regression.** Fleet and HR each grant anonymous exactly one right, so both
would flip from 401 to 404 and lose the sign-in redirect entirely (the interceptor reacts to 401
only). Five E2E assertions would go red.

The predicate is `SparkModuleRegistry.IdentityUserType != null || CredentialSchemes.Count > 0` —
the same condition `SparkMiddleware.cs:201-204` uses to decide whether `UseAuthentication()`
runs, so it cannot disagree with itself. See PRD D4. No spike needed; it is a two-test change
(M6), and the two tests are the ones nothing asserts today: the 404-for-everyone branch as its
own proposition, and a deny-all host still answering 401 to anonymous.

### S3 — The aliasing hazard is confirmed; the spike is now the TEST, not the question

`ModelLoader` is a Singleton (`:18`) and `GetEntityTypes()` (`:106`) hands every request
references into one mutable graph. Filtering in place is a permanent, process-wide,
first-caller-wins truncation. **This is no longer a question to answer but a test to write
first** — and it must be order-dependent across two requests on one host, because a
single-request test passes either way.

**Method:** stub `IPermissionService` to deny on the first call and allow on the second. Request
A → sub-query absent. Request B → sub-query **present**. Then resolve `IModelLoader` from the
same host and assert the singleton's `Queries` is still its original length.
**Pass:** all three. **Fail:** M9 needs a request-scoped projection layer, which is larger.

Add a second test for the catastrophic path: run `--spark-synchronize-model` after a request
that pruned, and assert the file on disk still lists every alias. That pins the near miss —
today the synchronizer is safe only because it re-reads the directory itself.

### S4 — Answered: prune on null `EntityType`, keep an unresolvable alias

Two different failures that must not be conflated:

- **`query.EntityType is null` → prune** (fail closed), matching `Queries/Get.cs:24`. Keeping it
  would render a sub-query that then 404s — precisely the bug being fixed, preserved for the one
  case nobody tests.
- **alias resolves to no query → keep, and warn.** A typo in `persistentObject.queries` is an
  authoring bug; pruning makes it invisible instead of loud, and buys no security because an
  unresolvable alias names nothing.

Note the divergence to record rather than reproduce: for `Database.*` queries the executor
authorizes the type resolved from the SparkContext property's generic argument
(`QueryExecutor.cs:132-138`), not `query.EntityType`. Prune on `query.EntityType` anyway — it is
what `getQuery`, the first call the sub-query makes, gates on.

### ~~S5~~ — answered: zero denials exist anywhere

No committed `security.json` in this repo or Coverage has ever contained `isDenied: true`,
across all branches and all history. No deployed file changes meaning. The risk is not
compatibility, it is the **ordering trap** — see M8, which is a restructure rather than a patch
precisely so the trap cannot be hit.

### S6 — Does the startup gate fire for apps and not for `SparkTestDriver`?

`SparkTestDriver` never calls `AddSpark`; `SparkEndpointFactory` does.

**Method:** place the gate, then run one driver-only test and one factory test.
**Pass:** the driver test is untouched, the factory test passes on the synthesised file, and a
demo with the file deleted refuses to start with a message naming the generator flag.
**Fail:** the gate is in the wrong call site.

### S7 — Do the two new demo files actually exercise `Query`-without-`Read`?

**Method:** after M10, run DemoApp and open the Stock grid. Assert rows list and **no** row is
a link; then grant `Read` temporarily and confirm the link appears.
**Pass:** observed both ways. This is the demo that makes #310's whole premise visible, so it
gets checked in a browser, not just asserted.
**Fail:** something else is granting `Read` transitively — find it.

### Not spiked: whether the click-through mechanism works

Already traced end to end on master (PRD F1), in both grids. Nothing left to measure.

### Not spiked: whether the access-control half can leave the package

Verified: `RavenDB.Client` appears only in the identity files; none of the six access-control
files touches Raven, Identity or JwtBearer, and `SecurityConfiguration` depends only on
`TranslatedString`, already in Abstractions.

---

## M2 — Models and loader to core

`SecurityConfiguration`, `Right`, `ISecurityConfigurationLoader` → `Abstractions/Authorization/`.
`SecurityConfigurationLoader` → `MintPlayer.Spark/Services/`, beside its structural twin
`CustomActionsConfigurationLoader`. Preserve the **Singleton** lifetime and the validate-on-every-load
behaviour, including hot reload — the file is constant in production, but the watcher costs
nothing and catches a bad edit in development.

Publish `SparkWellKnownGroups` with the two reserved names; they are `internal` constants on
the validator today.

## M3 — Evaluator, validator and claims provider to core

`AccessControlService` → `SecurityFileAccessControl` in `MintPlayer.Spark/Services/`, **Scoped**
(F9 — it reads `IHttpContextAccessor` for the authenticated/anonymous decision; a singleton
silently breaks that). `SecurityConfigurationValidator` and the bundle table move with it —
the validator deliberately reads the table from the evaluator so the two cannot disagree.
`ClaimsGroupMembershipProvider` (all five claim types) and `SecurityPostureReporter` move too;
the latter's interface is already in core.

`SparkAuthorizeAttribute` + handler move, keeping the singleton-handler-resolving-scoped-service
shape exactly.

## M4 — Registration

`AddSpark` registers loader, evaluator, claims provider and posture reporter unconditionally.
`AuthorizationOptions` folds into `SparkOptions`; **drop `DefaultBehavior`** — `AllowAll` is a
documented fail-open switch whose only user is WebhooksDemo, which gets a real file in M10.

`AddAuthorization()` is **deleted**, not emptied — M2/M3 take everything it did, and what
remains belongs to `AddAuthentication<TUser>()`. Update `SparkFullGenerator.Producer.cs:102-103`
in the same commit; it emits the call literally and every `AddSparkFull` app fails to compile
otherwise.

`Right.IsImportant` **survives and gets implemented** (PRD D8) — an earlier draft deleted it as
dead code, which confused unimplemented with unwanted.

## M5 — Startup gate and generator

WARNING: the starter flag is **`--spark-init-security`**. `--spark-synchronize-security` is
already taken by the posture baseline (`SparkSecurityVerificationExtensions.cs:23`), and that
command resolves the reporter, which loads the configuration — so under the new loader it would
throw on the very missing file a starter is meant to create.

A missing or malformed `security.json` refuses startup, in the same shape as the
`modelHashes.json` gate — **not** Vidyano's static-constructor throw, which surfaces as a 500
on an unrelated request later.

`--spark-synchronize-security` writes a starter file: `wellKnown` with both roles, both groups
named, one commented example grant. This is the only authoring support that will exist
(PRD F12), so the generated comments carry the grammar.

Wire the flag into all four demos' `Program.cs`, beside the model synchronize call they all
already have.

## M6 — Delete `AllowAnonymousAccess()`, fix the denial predicate

Delete `AllowAllAccessControl`, `DenyAllAccessControl` and the builder extension.

**Same commit:** replace `SparkDenial`'s type-sniff with the registry predicate (PRD D4) —
`IdentityUserType != null || CredentialSchemes.Count > 0`, with the null branch defaulting to
401 and a comment saying which way it fails. If that lands separately the M-3 oracle policy
shifts silently, which is the exact failure class this repo has been closing all month.

Two tests nothing asserts today: the 404-for-everyone branch as its own proposition, and a
deny-all host still answering 401 to anonymous.

While here, close the two refusals that still name what they refused (PRD F16): `GetPermissions`
has no authorization check at all, and `ExecuteCustomAction.cs:81,:89` return hardcoded 404s
naming the missing action.

## M7 — Test infrastructure

`SparkEndpointFactory` gains one optional last parameter, `SparkTestSecurity? security = null`,
defaulting to permissive — so all 33 direct construction sites compile untouched. The file is
written into `_contentRoot/App_Data/` in the **constructor**, beside the model directory:
unlike the model hash it depends on nothing `AddSpark` registered, and it must exist before
`_host.Start()`.

`SparkTestSecurity` publishes `Permissive`, `Empty`, `FromFile`, `FromJson`, `Build()` and
`Permissive.Without(...)`. Plus `SparkTestSecurityFile.Write(contentRoot)` for the one
hand-rolled host (`Builder/UseSparkOptionsTests.cs`), symmetric with `WriteSparkModelHashes`,
which that file already calls for exactly the same reason.

**Build the grant builder.** Fleet's file is 155 lines for 19 rights, and every grant costs
three GUIDs. Group and right ids derive **deterministically** from their names — never
`Guid.NewGuid()`, or every run writes a different file, posture snapshots churn, and the
duplicate-id validator fires on randomness instead of on a real duplicate. `wellKnown` is
emitted automatically so the well-known validators cannot fire on a builder-produced file.

**The ordering guarantee changes shape.** Today it is "last DI registration wins, and the caller
is last". It becomes: the **file** is the normal override seam, written from the caller's value;
`configureServices` still runs last, so a test may additionally swap `IAccessControl` wholesale
for the two cases that need a predicate rather than a grant list.

Publish `SparkTestAccessControl` (`AllowAll`/`DenyAll`/`Granting`/`Matching`, plus an `Asked`
list absorbing the hand-rolled recording fake) and a `services.UseSparkTestAccessControl(...)`
extension, so the `RemoveAll` + `AddSingleton` idiom is written once. **Do not publish an
`IPermissionService` double** — it is four lines of string concatenation, and faking it removes
the one piece of logic a resource-string assertion needs to keep honest.

**The deny-all mirror suite (R17).** One table-driven class on `SparkTestSecurity.Empty`
asserting every Spark endpoint refuses: PO get/list/create/update/delete, queries
get/list/execute/stream, actions list/execute, lookup references, permissions, entity types,
aliases, program units. ~20 rows, one host. It is the only thing that turns a deleted permission
check into a red build, and it reaches the `isAuthed ? 403 : 401` branch that only E2E covers
today.

**The factory asserts its own file loaded** after `_host.Start()` (R18). A silently bypassed
file would make the override mechanism a no-op and every authorization test vacuously green.

Known casualties, planned not discovered: `Authorization/PermissionServiceDefaultsTests.cs` is
deleted outright (its whole subject is the three-way DI default), and
`SecurityConfigurationLoaderTests` pins the warn-and-return-empty behaviour D3 replaces.

## M8 — Symmetric deny expansion

**A restructure, not a patch.** Build a derived, memoised index on the **loader** — per group,
`(Allowed, Denied)` frozen sets with bundles already expanded through one shared `Expand()`
helper — and reduce evaluation to two set probes, denials first. Symmetry then cannot be
re-broken, and the per-check `List` allocation on every call disappears.

⚠️ Appending "expanded denials" as a fourth step to the existing chain makes `grant Read/Car` +
`deny QueryReadEditNewDelete/Car` return **true**, because the exact grant fires first. All
denial matching must precede all grant matching.

Keep parse-then-lookup on the action segment; do **not** reach for `StartsWith` when writing the
set builder, or `NewDeleteAttachment/Car` becomes `NewAttachment/Car` + `DeleteAttachment/Car`
and the written right vanishes.

Delete the validator rule rejecting combined denials, and rewrite the `<remarks>` at
`AccessControlService.cs:22-27` that documents the asymmetry as intentional — otherwise the next
reader restores the filter. `SecurityPostureReporter` must read the expanded index too, or it
overstates the anonymous surface.

Precedence, with `IsImportant` (PRD D8) landing in the same set-based rewrite:
**Important → denial → grant(exact) → grant(expanded) → refuse.** Build all four tiers at once;
retrofitting an override into a two-set probe later means touching the same code twice.

To be stated in the guide: **a denial is absolute unless an important right overrides it.** It
cannot otherwise be re-granted by adding a group, and a mistaken denial on `authenticated`
locks out administrators. Rewrite `Right.cs:42-46`, which currently promises audit logging and
would make an override look like a tagging convention.

## M9 — Memoise, then prune

**First**, a request-scoped `Dictionary<string,bool>` inside `PermissionService` (already
Scoped). Never process-wide: `AccessControlService.cs:114` reads authentication state from
`IHttpContextAccessor`, so a shared cache is a cross-user leak. This alone speeds up
`EntityTypes/List`, `GetAliases`, `ProgramUnits/Get` and `GetPermissions`.

**Then** filter `EntityTypeDefinition.Queries` in **both** `EntityTypes/List.cs` and `Get.cs` —
`List` is the load-bearing one, since `spark-po-detail` reads the array and never calls
`getEntityType(id)`.

- Copy **only when something is pruned**; return the same reference otherwise. A shallow copy
  shares `Attributes`/`Tabs`/`Groups`, which is fine as long as nothing prunes those in place —
  write the helper so the next author inherits the copy, not the aliasing.
- Null `EntityType` → prune. Unresolvable alias → keep and warn (S4).
- `List.cs` needs `IQueryLoader` injected; both loaders are singletons.

No client change is required — `@if (et.queries?.length)` already guards the loop and nothing
assumes a fixed set.

## M10 — Demo security files

**Content is already derived: `docs/issue_310_demo_security.md`.** Both files, complete, with a
right-by-right rationale and the four grants that only surface at runtime. M10 is copy-in plus
the `Program.cs` changes, not authoring.

All four demos ship one. DemoApp and WebhooksDemo are new; Fleet and HR may need additions
once `AllowAll` is gone. At least one type per new file demonstrates `Query`-without-`Read`
(Stock and ProjectColumn are the natural candidates — both have rows that no detail page could
ever load).

⚠️ Fleet's file is consumed by the out-of-process E2E host; changing it can break 71 E2E tests.

## M11 — Wire the security gate

`VerifySparkSecurityIfRequested` into all four demos, `securityPosture.txt` baselines
committed, and a CI step. The gate has existed in core since preview.5x and **no host calls
it** — every app currently fails it for want of a baseline.

## M12 — Docs

The package README documents a `security.json` shape the validator **rejects at startup** —
rewrite it. Fix the three statements that describe the deleted `Everyone` semantics:
`SecurityConfiguration.cs:24` ("every caller, signed in or not"),
`SecurityConfigurationValidator.cs:150-156`, `AccessControlService.cs:124`. Update
`docs/guide-authentication-schemes.md`, `docs/guide-custom-actions.md:185`, the testing README,
and `Demo/HR/HR/HRContext.cs:19`.

Add a guide section on `Query` vs `QueryRead` and what each does to the grid — the mechanism
this whole issue exists to make visible.

## M13 — Release

`10.0.0-preview.62` across the `.csproj` files; npm only if the client changed. Release notes
lead with the breaking changes: mandatory file, `AddAuthorization`/`AllowAnonymousAccess` gone,
namespaces moved, `DefaultBehavior` dropped.

---

## Verification

- Full .NET suite, once, at the end; the row-security suites re-run in isolation (they are
  flaky under load, and M3 moves the code they exercise).
- `nx run @mintplayer/ng-spark:test` if M1's client fixes come across.
- Manual, in a browser: S7's `Query`-without-`Read` check on DemoApp.
- Each demo booted once with its new file, and once with the file deleted to see the gate fire.

## Open questions

1. **Does `AddAuthorization()` survive at all**, or does everything left fold into
   `AddAuthentication<TUser>()`? Decide after M3 empties it.
2. **Should the hot-reload watcher survive?** The owner says the file is constant once
   deployed, and a VSCode extension will edit it at authoring time. The watcher costs little
   and helps development; keeping it is the default unless it complicates the startup gate.
3. **`Machine:`/`Module:` prefixes** (PRD F8) — `Module:` has a code constant, `Machine:` is a
   bare convention. Worth a constant and a validator rule here, or its own issue?

## Outcome

_(filled in as milestones land)_
