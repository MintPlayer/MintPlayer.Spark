# PRD — `security.json` moves into Spark core

**Status:** Planned
**Issue:** [#310](https://github.com/MintPlayer/MintPlayer.Spark/issues/310)
**Branch:** `feat/security-json-in-core`
**Plan:** `docs/issue_310_plan.md`
**Base:** `master` @ `7ad2e30`
**Release:** `10.0.0-preview.62` (server); npm only if the client changes
**Breaking changes:** allowed and used — the libraries are in preview

---

## Problem

Authorization is not optional for a framework whose whole job is serving data, but Spark
treats it as one. `security.json`, the group model and the evaluator all live in the optional
`MintPlayer.Spark.Authorization` package. An app that never calls `AddAuthorization()` gets
`DenyAllAccessControl` — everything refused — and one opt-in line replaces that with
`AllowAllAccessControl`, which permits everything and never reads a file.

The consequence is not theoretical. **In two of four demos the rights model is inert**:
DemoApp calls `AllowAnonymousAccess()` and WebhooksDemo runs `AddAuthorization` with
`DefaultBehavior = AllowAll` — and neither ships a `security.json` at all. In those apps
`canRead` is unconditionally true, so nothing rights are supposed to express can be
demonstrated, or even noticed when it is missing.

## Origin

Work on #309 produced `SparkQuery.rowsNavigable`, a model-file flag to suppress a grid's
click-through to a detail page. The owner rejected it: in Vidyano this is controlled by
grants — omit `Read` and no link renders.

**They were right, and more strongly than stated: the mechanism already exists and already
works.** See F1. `rowsNavigable` was a second authority over a decision the rights model
already makes, and the two could disagree with the model file silently winning.

The reason the existing mechanism was not obvious is F2: the demos that would have shown it
have no security configuration at all.

## Investigation findings

Four investigations. Everything below was read in the code.

### F1 — The click-through capability is already shipped, end to end

Traced on `master`:

1. `AccessControlService.cs:33` — `["QueryRead"] = ["Query", "Read"]`, matched at `:212-224`.
   `Query/X` satisfies only `Query`; it never satisfies a `Read/X` request.
2. `PermissionService.cs:17` — `var resource = $"{action}/{target}"`.
3. `Endpoints/Permissions/GetPermissions.cs:32-37` — `canRead = IsAllowedAsync("Read", target)`.
4. `spark.service.ts:33-35` → `canRead` signal (`spark-query-list.component.ts:169`,
   `spark-sub-query.component.ts:94`).
5. `spark-query-list.component.html:92`/`:124` and `spark-sub-query.component.html:27` —
   `@if (first && canRead())`.

**Granting `Query/X` without `Read/X` already renders the grid with no anchor.** The listing
side is cleanly separable: every execute path asks only for `Query`
(`QueryExecutor.cs:138,239`, `Queries/Get.cs:25`, `List.cs:26`), and `Read` is required in
exactly one place — `DatabaseAccess.cs:84`, the detail load, which is what the link points at.

Coverage grants `QueryRead/MyAccountRow` (`security.json:11`); that is why it gets a link.
Changing it to `Query/MyAccountRow` suppresses the link and still lists rows.

### F2 — Two of four demos cannot demonstrate any of this

| App | Opt-in | `security.json` | Effective |
|---|---|---|---|
| Fleet | via `AddSparkFull` | ✅ 19 rights, `wellKnown` block | real |
| HR | `AddAuthorization()` | ✅ 26 rights | real |
| DemoApp | `AllowAnonymousAccess()` (`Program.cs:37`) | ❌ none | everything allowed |
| WebhooksDemo | `AddAuthorization` + `AllowAll` (`Program.cs:48`) | ❌ none | everything allowed |

`AllowAnonymousAccess()` has exactly **two** call sites in the repo: DemoApp and
`SparkEndpointFactory.cs:99`.

### F3 — Vidyano confirms the model, and Spark is ahead of it in the places that matter

Vidyano's `SecurityScope.cs:31-69` has the same ten bundles with the same expansions.
`VidyanoModelContext.cs:386-394` sets `query.CanRead = PersistentObject.HasRight("Read")` and
filters out queries failing `HasRight()` entirely; `query-grid.ts:589` gates navigation on
`query.canRead`. Its `security.json` is **mandatory** — `VidyanoDbCache.cs:397` throws.

**Spark is already ahead** on: row-level security with filter pushdown and WITH CHECK, per-row
`can` flags, the `anonymous`/`authenticated` distinction (Vidyano has neither — no user means
an empty claim set), a config validated on every load, and a denial path that does not leak.
**Vidyano answers a denial with HTTP 200** and a body naming the type and the missing right
(`WebController.cs:3197`, `culture.json:748`). Spark's uniform 404 is audit finding M-3 and
must not be softened to match.

**Vidyano is ahead** on four things, all candidates but only one in scope here:

| Capability | Vidyano | In scope? |
|---|---|---|
| Symmetric deny expansion | `ClaimSet.Add` expands grants *and* denials (`:598-621`) | **yes** — small, and Spark's validator currently rejects the natural "grant all, deny delete" |
| Attribute-level rights (`Action/Type/Attribute`) | used in production | no — own issue |
| Schema-level grants (`Action/Schema`) | yes | no — Spark has no schema concept |
| Pruning-by-omission in the PO payload | queries you cannot `Query` are deleted from the payload | **yes** — F7 |

### F4 — The core side knows nothing about files, groups or rights

The entire core surface is one string and a bool: `IAccessControl.IsAllowedAsync(resource)`
(`Abstractions/Authorization/IAccessControl.cs:7-22`) and `IPermissionService`
(`:8-31`). `PermissionService.cs:6-27` is the only implementation and does nothing but
concatenate. Every consumer goes through `IPermissionService`, never `IAccessControl`.

**Core already loads three `App_Data` JSON files** — `ModelLoader.cs:39`,
`CultureLoader.cs:21`, `CustomActionsConfigurationLoader.cs:23`. The last is structurally the
same class as the package's `SecurityConfigurationLoader`: `IMemoryCache` + `FileSystemWatcher`
+ `IHostEnvironment.ContentRootPath`. Core owning a security file is not a new capability class
for core.

Core also already declares `ISecurityPostureReporter` and ships
`SparkSecurityVerificationExtensions` (`--spark-verify-security`, baseline
`App_Data/securityPosture.txt`) — i.e. **core already describes security.json's contents from
the outside**. Moving the file in removes an inversion rather than creating one.

### F5 — The package splits cleanly, and the line is RavenDB

`RavenDB.Client` appears in exactly `Identity/{UserStore,RoleStore,SparkUser,SparkRole}.cs`
and the two auth extension files. **None** of the six access-control files touches Raven,
Identity or JwtBearer. `SecurityConfiguration` depends only on `TranslatedString`, which is
already in Abstractions. The package references only Abstractions, never `MintPlayer.Spark`,
so there is no cycle either way.

Moving the identity half would drag `Microsoft.AspNetCore.Identity`,
`AddIdentityApiEndpoints`, JwtBearer and a fixed RavenDB document schema into every Spark app.
That is the line.

### F6 — `Everyone` is not coming back

Deleted in `f28cfe0` (#298/#301, merged `50577bb` → preview.60) for two documented reasons:
well-known groups resolved via `TranslatedString.GetDefaultValue()` = first translation **in
file order**, so reordering two JSON keys silently changed authorization; and the token was
dishonest — it meant "the public internet" and read as "my users". The decision was explicit:
*"a clean break, not a shim"*.

`SecurityConfigurationValidator.cs:94-115` rejects it, firing only on a file with no
`wellKnown` block. The **display name** "Everyone" is permitted once `wellKnown` exists; the
token and its semantics are not.

**Owner confirmed:** core ships `anonymous` + `authenticated`. "All callers" remains two grants.

### F7 — Sub-queries are not pruned by right (a UX defect, not a hole)

Vidyano deletes a sub-query the caller cannot `Query` from the payload
(`VidyanoModelContext.cs:386`). Spark ships the full `EntityTypeDefinition.Queries` list and
lets the client discover the refusal.

**Correction to an earlier draft of this PRD:** the grid does *not* mount and then fail.
`spark-sub-query` calls `getQuery(alias)` first, `Queries/Get.cs:24` already gates that on
`Query/{EntityType}` and 404s, `loadData`'s catch leaves `query` null, and the template renders
nothing. So what ships today is **a wasted round-trip per unauthorised sub-query, a console
404, and an empty gap in the page** — noise and confusion, not a disclosure. Worth fixing;
not a security fix, and this PRD should not claim otherwise.

⚠️ **`EntityTypeDefinition.Queries` is a `string[]` of aliases on a SINGLETON, shared by every
request.** `ModelLoader` is `ServiceLifetime.Singleton` (`:18`) and `GetEntityTypes()` (`:106`)
hands out references into one mutable object graph. Filtering in place would be a permanent,
process-wide, first-caller-wins truncation: the first anonymous request strips a sub-query for
every admin thereafter, with no error and no log, recoverable only by restarting the process.

⚠️ **And one refactor away from catastrophe.** `ModelSynchronizer` mutates a definition in place
and writes it **back to disk** (`:186-196`). It survives only because it re-reads the model
directory itself rather than using `IModelLoader`. Wire it to the loader — in a process that
also runs `--spark-synchronize-model`, which all four demos do at build time — and a pruned
list is written to the model file permanently. Closed today by an accident, not an invariant.

Also unpruned, and out of scope: `EntityAttributeDefinition.Query` — the single alias an
AsDetail or reference attribute names. A reference dropdown whose lookup query the caller
cannot `Query` still renders and still fails.

### F8 — Weak links this overhaul makes universal

- **Group membership is name-matched.** `AccessControlService.cs:168-194` matches a claim
  string against **any translation** of a group's display name, case-insensitively. `wellKnown`
  protects the two reserved roles (`:184-188`); every application group is still claimable by
  its Dutch label. Vidyano avoided this by resolving membership to group objects with ids.
  Today this is opt-in; after this change it is the universal path.
- **`Machine:` is a bare convention.** `Module:` has `SparkModuleCertificateDefaults.GroupPrefix`;
  `Machine:FleetApi` in Fleet's file (`:36`) has nothing enforcing it. A typo is an outage with
  no diagnostic.
- **The IdentityProvider mints the group claims** the evaluator resolves
  (`OidcTokenGenerator.cs:96-113`), deliberately unprefixed and only for `client_credentials`
  — its comment records that namespacing them silently defeated authorization and that merging
  them on a delegated grant would let consent make an end user an Administrator.
- **`docs/prd/PRD-SecurityAudit.md:202`** already flags an untrusted external `group`-claim
  issuer as a known hazard.

### F9 — Lifetimes differ across the code being moved

Loader is **Singleton** (`SecurityConfigurationLoader.cs:10`), evaluator is **Scoped**
(`AccessControlService.cs:10`), and `SparkAuthorizeHandler` is a **singleton that re-resolves
`IAccessControl` from the request scope per evaluation** (`SparkAuthorizeAttribute.cs:89-119`).
A move that makes the evaluator a singleton silently breaks the `IHttpContextAccessor`-derived
authentication state at `AccessControlService.cs:114`.

### F10 — The startup-gate precedent is already wired, for the model and not for security

`SynchronizeSparkModelsIfRequested(args)` is called by **all four** demos (Fleet `:126`,
HR `:70`, DemoApp `:48`, WebhooksDemo `:101`). `VerifySparkSecurityIfRequested` is called by
**none** — it was written and never plugged in. No `securityPosture.txt` baseline is committed
anywhere, so every app currently fails that gate.

### F11 — Test blast radius

- `SparkTestDriver` has **zero** authorization involvement — it never calls `AddSpark`.
  ~80 subclasses / ~474 methods, unaffected unless the requirement is enforced at startup.
- `SparkEndpointFactory` boots hosts, calls `AllowAnonymousAccess()` at `:99` **before** the
  caller's `configureSpark` (ordering is load-bearing), and writes no `security.json`. Its
  content root is a per-test temp dir; `WriteSparkModelHashes` (`:113`) is the precedent for
  synthesising an `App_Data` file.
- **34 files / ~163 methods** go through the factory; ~20 more call `AddSpark` directly.
- E2E (25 files / 71 tests) runs the real Fleet app against its committed file — unaffected.
- No `IAccessControl` fake exists in `libs/testing`.

### F12 — No authoring support

No `*.schema.json` anywhere, no `$schema` in any security file, `extensions/vscode` has no
tracked files. A file that becomes mandatory for every app, is hand-authored, and has ~9
distinct validator failure modes needs a starter generator at minimum.

### F13 — No backward compatibility is required

Owner directive. Nothing in this PR needs a shim, an `[Obsolete]` marker, a compatibility
overload or a migration window. `AddAuthorization()` and `AllowAnonymousAccess()` are deleted
outright, namespaces move without forwarding types, and `AuthorizationOptions.DefaultBehavior` is
deleted rather than defaulted.

**`Right.IsImportant` is NOT deleted** — an earlier draft of this PRD dropped it as dead code,
which was wrong: nothing reading it makes it *unimplemented*, not unwanted. See D8.

Two consequences worth naming, because "no compatibility" does not mean "nothing must keep
compiling":

- **`SparkFullGenerator.Producer.cs:102-103` emits a literal
  `SparkBuilderAuthorizationExtensions.AddAuthorization(spark, options.Authorization)`.** Every
  `AddSparkFull` app, Fleet included, breaks the instant that method disappears. The generator
  is updated in the same commit.
- **Fleet's `security.json` is load-bearing for 71 E2E tests** in ways invisible from reading
  it: the literal display names `Administrators`, `Fleet managers`, `Machine:FleetApi` and
  `Module:HR` are asserted, and so are two *absences* — no `Car` grant to anonymous
  (`AnonymousPersistentObjectAccessTests.cs:45`) and no `Edit/LookupReferences` grant to anyone
  (`LookupReferenceAuthTests.cs:9-11`). Changing that file is a test change, not a config
  change.

### F14 — Nobody has ever written a denial

Across every branch and the whole history of this repo and Coverage, **no committed
`security.json` contains `isDenied: true`**. Denials exist only in C# tests. So D5's symmetric
expansion changes the meaning of exactly zero deployed files.

### F15 — Permission checks are unmemoised, and the fix belongs one layer down

Every `IsAllowedAsync` re-enumerates group membership and does a **linear scan of the entire
rights table, materialised into a new list** (`AccessControlService.cs:133-135`). `List.cs`
already runs one per entity type on every page load; D6 multiplies that by sub-queries.

A request-scoped `Dictionary<string,bool>` inside `PermissionService` (already `Scoped`) fixes
`EntityTypes/List`, `Aliases/GetAliases`, `ProgramUnits/Get` and `GetPermissions` at once, and
makes D6 free. **It must not be process-wide**: `AccessControlService.cs:114` derives
authentication state from `IHttpContextAccessor`, so a shared cache is a cross-user leak — the
same class of bug as F7's aliasing hazard. `IRowSecurity`'s per-request memo is the precedent.

### F16 — Two refusals still name what they refused

Unrelated to the move, in the same family as M-3: `GetPermissions` has **no authorization check
at all** and returns all five booleans to any caller, and `ExecuteCustomAction.cs:81,:89`
return hardcoded 404s whose body names the missing action, bypassing `SparkDenial`'s constant
message. Both are cheap to close here.

### F17 — Rights that only surface at runtime

Deriving the two new demo files turned up four grants that no reading of the screens would
suggest, each of which fails silently rather than loudly:

- **`Query/{ChildType}` for every AsDetail child.** The child's *definition* is resolved from
  the `Query`-filtered type list, so without it the embedded section renders blank.
- **`NewDelete/{ChildType}`** for an editable AsDetail grid — the add/remove-row buttons read
  `canCreate`/`canDelete` on the **child** type, not the parent.
- **`Read/LookupReferences`** — a literal target. Without it every lookup dropdown is empty.
- **`Query/{ReferencedType}`** for a reference picker, which is a different requirement from
  the referenced type appearing in a menu.

The guide must list these, or every app will hit them one at a time.

### F18 — "Permissive" is not expressible as data, so the wildcard has to exist

`MatchesResource` (`AccessControlService.cs:196-199`) is exact string equality. A grant-everything
file would have to enumerate every resource — and the resources are not enumerable: custom
action names arrive as route values (`ExecuteCustomAction.cs:52`), `LookupReferences` is a
literal target, and modules add their own (`Replicate/Cars`).

So the test harness cannot express its default, and neither can an app that wants an open
surface. Add a two-segment wildcard — `*/*`, `Action/*` — matched per segment. It already passes
the validator unchanged (it has a slash, both sides non-empty), composes with bundle expansion
and with denial-first, and **renders verbatim in the posture report**, so a wide-open app is a
one-line diff a reviewer cannot miss.

Then delete `DefaultBehavior.AllowAll`: with a wildcard there is exactly one way to be
permissive and it is written in the file. Keeping both is a second authority outside the file —
the same mistake as `rowsNavigable`, and it would silently poison the override path, since a
test supplying a restrictive file would still pass everything unless it also flipped the option.

Consider refusing `*` on the **action** half while allowing `*/*` and `Action/*`: a target
wildcard says "this group administers everything of this kind", whereas `*/{Target}` grants
every custom action anyone adds in future.

### F19 — The test blast radius is 244, not 163, and 234 of them are granted everything

`IdentityProvider/OidcTestHost.cs:41` boots one factory host that **11 subclasses inherit**,
adding 144 tests the earlier count missed. Of the 244, exactly **10** override the security
layer.

**The permissive default does not weaken anything — `SparkEndpointFactory.cs:99` already calls
`AllowAnonymousAccess()`, so this is the status quo.** But it is worth stating plainly: delete
`EnsureAuthorizedAsync("Read", …)` from `DatabaseAccess.cs:84` today and every factory-booted
test still passes. **No test anywhere asserts "endpoint X consults the permission service."**
The only real net is `E2E/Security/*` (21 files, 60 tests) against Fleet's committed file, which
covers nothing in `Aliases`, `EntityTypes/List`, `Queries/List`, `Queries/Get` or `ProgramUnits`.

This milestone is the cheapest opportunity to fix that, and the fix is not logging — a warning
on all 244 hosts is noise by construction. It is **one deny-all mirror suite**: a table-driven
class booting the factory with a valid zero-rights file and asserting every Spark endpoint
refuses. ~20 rows, one host, and it is also the direct test for acceptance criteria 7 and 9.

## Requirements

| # | Requirement |
|---|---|
| R1 | `App_Data/security.json` is loaded and enforced by core, with no package reference |
| R2 | `anonymous` and `authenticated` are core concepts; `Everyone` stays deleted |
| R3 | A missing or malformed file **refuses startup**, with a message naming the fix |
| R4 | `AllowAnonymousAccess()` is deleted; every app has a real file |
| R5 | Custom groups resolve through `IGroupMembershipProvider`, defined in core, implemented by the package |
| R6 | The Authorization package keeps identity and gains nothing it does not need |
| R7 | The M-3 denial contract survives the deletion of `AllowAllAccessControl` |
| R8 | The test driver writes a permissive default; a test can override by path or inline JSON |
| R9 | Deny expands symmetrically with grant |
| R10 | Sub-queries the caller cannot `Query` are pruned server-side |
| R11 | `rowsNavigable` never ships |
| R12 | Every demo has a `security.json` that demonstrates the model, including `Query`-without-`Read` |
| R13 | No shims, no `[Obsolete]`, no compatibility overloads — deleted means deleted |
| R14 | Permission decisions are memoised per request, never across requests |
| R15 | Pruning never mutates the shared model graph, and never reaches the synchronizer |
| R16 | A wildcard resource exists, and `DefaultBehavior` is deleted |
| R17 | A deny-all mirror suite asserts that every endpoint refuses when nothing is granted |
| R18 | The test factory proves the file it wrote was actually loaded |
| R19 | An important right overrides everything, including denials, and is reported separately |

## Design

### D1 — What moves

To `MintPlayer.Spark.Abstractions/Authorization/`: `SecurityConfiguration`, `Right`,
`ISecurityConfigurationLoader`, and a public `SparkWellKnownGroups` exposing the two reserved
names (today they are `internal` constants on the validator).

To `MintPlayer.Spark/Services/`: `SecurityConfigurationLoader`, `SecurityConfigurationValidator`,
the evaluator (renamed `SecurityFileAccessControl`), the bundle table,
`ClaimsGroupMembershipProvider`, `SecurityPostureReporter`. `SparkAuthorizeAttribute` +
handler move to core. `AuthorizationOptions` folds into `SparkOptions`.

`AddSpark` registers all of it unconditionally. Lifetimes are preserved exactly (F9).

### D2 — What the package becomes

Identity and authentication only: RavenDB `UserStore`/`RoleStore`, the `SparkUser`/`SparkRole`
documents, ASP.NET Identity wiring, `SparkLocalCredentials`, GitHub/OIDC/JWT schemes, the
`/spark/auth` endpoints, the MSBuild targets — plus an Identity-roles-backed
`IGroupMembershipProvider`. Core needs `IsAuthenticated` and a set of group names; it does not
need to know where either came from.

### D3 — Missing file refuses startup

Matching the `modelHashes.json` gate, and Vidyano's intent — but as a **startup gate**, not
Vidyano's static-constructor throw, which surfaces as a 500 on an unrelated request.

**The throw belongs in the loader, not in a static verifier**, replacing
`SecurityConfigurationLoader.cs:57-61`'s warn-and-return-empty. The loader already validates on
every load so that *"a file that has drifted into meaninglessness must not quietly replace one
that had not"* — a file **deleted** at runtime is the same hazard, and today it silently
degrades a running app to empty-config five minutes later via the cache. `UseSpark` then forces
the first load eagerly, so the failure lands at startup rather than on whichever request happens
to be first.

**Three deliberate differences from the model gate:** no Development exemption (drift there is
normal for models — you author `security.json` once, and a dev-only warning means the app boots
deny-all locally and refuses in CI), no override environment variable (a security equivalent of
`SPARK_MODEL_HASH_OVERRIDE` is a boolean off-switch for authorization, which is what R4 deletes
`AllowAnonymousAccess()` to avoid), and the gate lives in `UseSpark`, never in `AddSpark` — 19
builder tests inspect the `ServiceCollection` without ever building a host.

`--spark-synchronize-security` generates a starter file: `wellKnown` with both roles, both
groups named, and a commented example grant. That closes F12's authoring gap with the
mechanism the repo already uses for models.

`AllowAnonymousAccess()` is deleted rather than redefined (R4). An app that genuinely wants an
open surface writes a file granting to `anonymous` — which is honest, greppable, and shows up
in the posture report.

### D4 — `SparkDenial` asks whether anyone can authenticate at all

⚠️ **`SparkDenial.cs` does not exist on `master` or on this branch.** It arrives with M1's
cherry-pick from #308, where `:85` decides 401-vs-404 by type-sniffing `AllowAllAccessControl`
— which means *"anonymous can reach **everything**"*. Until M1 lands, the decision is made
per-endpoint from `httpContext.User.Identity?.IsAuthenticated`. D4 is therefore work on code
this PR introduces, not on code it finds; schedule it after M1 or the milestone budgets a file
that is not there.

**An earlier draft of this PRD proposed "does the `anonymous` group hold any grant?" — that is
wrong and would have shipped a silent regression.** It turns *everything* into *something*.
Fleet and HR each grant anonymous exactly one right (`QueryRead/Company`), so both would flip
from 401 to 404 — and `spark-auth.interceptor.ts:14-28` reacts to **401 only**. The sign-in
redirect would vanish for any app with a single public grant, five E2E assertions would go
red, and in production it reads as a data bug rather than an auth bug.

Per-resource ("does anonymous hold THIS grant?") gets the outcomes right but is disqualified
twice: the status would vary with the requested resource, letting an anonymous prober
enumerate the anonymous grant table — the invariant M-3 exists to establish is that status is a
function of the caller and never of the resource — and it would spread that invariant across
12 call sites, one of which (`PersistentObject/Get.cs:24`, unknown type) has no resource at all.

**The question is not about configuration. It is whether there is anywhere to sign in:**

```csharp
private static bool AuthenticatingWouldHelp(HttpContext httpContext)
    => httpContext.RequestServices.GetService<SparkModuleRegistry>() is not { } registry
       || registry.IdentityUserType != null
       || registry.CredentialSchemes.Count > 0;
```

Self-consistent by construction: this is the same condition `SparkMiddleware.cs:201-204` uses
to decide whether `UseAuthentication()` runs at all, so when it is false `IsAuthenticated` can
never become true and a 401 would be an instruction the application cannot satisfy. Synchronous,
resource-independent, and **untouched by #310** — no dependency on `security.json`,
`IAccessControl` or the group model. The null branch defaults to 401, matching today's
behaviour when `IAccessControl` is absent; that is a defaulting decision and carries a comment
saying so.

**This lands in the same commit as the deletion of `AllowAllAccessControl`**, or the oracle
policy changes silently.

### D5 — Symmetric deny expansion

Expand bundles for denials as well as grants. Vidyano does this in three lines
(`SecurityScope.cs:598-621`) and it makes "grant `QueryReadEditNewDelete/Car`, deny
`EditNewDelete/Car`" work — currently rejected at load by
`SecurityConfigurationValidator.cs:61-66`, a rule that then becomes unnecessary.

**Zero committed `security.json` in either repo has ever contained `isDenied: true`** — verified
across all branches and all history. The blast radius in deployed configuration is nil, which
is the argument for doing it now rather than after someone depends on the asymmetry.

⚠️ **The ordering trap.** Today's chain is denial(exact) → grant(exact) → grant(expanded).
Appending "denial(expanded)" as a fourth step makes `grant Read/Car` + `deny
QueryReadEditNewDelete/Car` return **true**, because the exact grant fires first. All denial
matching must precede all grant matching. Build the expansion into a derived, memoised index on
the **loader** (grants and denials through one `Expand()` helper, so symmetry cannot be
re-broken), and reduce evaluation to two set probes. A patch to the existing four-step chain
gets this wrong; a set-based rewrite cannot.

Do **not** copy Vidyano's `StartsWith` matching: a custom action named `NewDeleteAttachment`
would be torn into `New` + `DeleteAttachment` — the written right absent, two invented ones
present. Spark's parse-then-lookup on the action segment is immune; keep it when moving
expansion to load, where the temptation to reach for prefix matching is strongest.

`SecurityPostureReporter` must read the same expanded index, or it will overstate the anonymous
surface. No baseline is committed yet (F10), so this is the free moment.

### D6 — Prune sub-queries by right

`EntityTypeDefinition.Queries` is filtered server-side by `Query/{EntityType}` before the PO
payload is sent, so a caller who cannot query a sub-query does not get a grid that 404s.

### D7 — Group resolution stays claim-based, with the weakness recorded

Out of scope to fix (F8), but the PRD states it: application groups remain assertable by any
translation of their display name. The fix — resolving membership to ids like Vidyano does —
is its own issue, and it is more urgent after this change than before, because this makes the
claim path universal.

### D8 — `IsImportant` is implemented as an override tier, and its own doc comment is wrong

Owner decision: an important right **overrides every other right**, denials included.

⚠️ **Spark's field does not currently claim that.** `Right.cs:42-46` documents it as *"marks
this as an important/sensitive permission. Can be used for enhanced logging or audit
purposes."* — an audit marker, never implemented. The owner's semantics are Vidyano's, where
`Important` is a third precedence tier returning true unconditionally
(`SecurityScope.cs:191-201`). The comment must be rewritten in the same commit that implements
the behaviour, because anyone who took it at face value and set `isImportant: true` for audit
reasons would silently be minting an override.

**It fills a real gap.** `SparkSystemContext` exempts the system principal from row security
(`RowSecurity.cs:234,297,459`) and from the default actions, but **not** from type-level
authorization. Spark has no way for the framework to punch through a type-level rule — which is
Vidyano's principal internal use for `Important`: rendering a lookup dropdown whose type the
caller cannot query. Spark hits the same case (`Read/LookupReferences`, F17), and today the
only answer is "grant it to everyone".

**The cost, accepted deliberately.** Today *a denial is absolute*: read a deny and you know it
holds. With `Important`, you cannot — you must scan for Important grants on every group the
caller might belong to. Vidyano accepts this; its own production file carries zero Important
entries and zero denials, so in practice the tier is framework-internal there.

Mitigation rather than avoidance: `SecurityPostureReporter` surfaces Important grants
**prominently and separately**, since they are the one construct that can silently defeat a
deny, and they therefore belong in the `securityPosture.txt` baseline a reviewer diffs.

Precedence becomes: **Important → denial → grant (exact) → grant (expanded) → refuse.**

## Decisions

| Decision | Why |
|---|---|
| `security.json` in core | Authorization is not optional for a data framework; core already loads three App_Data files and already reports on this one |
| `anonymous` + `authenticated`, no `Everyone` | F6 — deleted deliberately, owner reconfirmed |
| Missing file refuses startup | A missing security file is never intentional; silently denying looks like a bug, silently allowing is a breach |
| `AllowAnonymousAccess()` deleted | An exception to "always used" would be the thing everyone reaches for |
| Package keeps identity | F5 — moving it drags Identity + a Raven schema into every app |
| Test driver writes a permissive default | F11 — 163 tests otherwise fail at host build; overridable per test |
| Deny expands symmetrically | F3 — a real gap, small fix, removes a validator rule |
| `rowsNavigable` deleted | F1 — proven redundant before it ever shipped |
| Attribute- and schema-level rights deferred | Real capabilities with real callers in Vidyano, but each is its own issue |
| `IsImportant` implemented, not deleted | D8 — unread is not unwanted, and it fills the type-level punch-through gap `SparkSystemContext` leaves open |
| Important grants are reported separately | They are the only thing that can defeat a deny; a reviewer must see them in the baseline diff |

## Acceptance criteria

1. An app with no `security.json` fails to start, naming the file and the generator flag.
2. `--spark-synchronize-security` writes a valid starter file that then boots.
3. A core-only app (no Authorization package reference) enforces grants from `security.json`.
4. `anonymous` and `authenticated` resolve in core; a file declaring `Everyone` with no
   `wellKnown` block is rejected with the existing migration message.
5. A custom group resolves only when an `IGroupMembershipProvider` is registered.
6. `Query/Car` without `Read/Car` lists cars and renders **no** row link; `QueryRead/Car`
   renders one. Asserted in both grids.
7. A sub-query the caller cannot `Query` is absent from the PO payload.
8. Grant `QueryReadEditNewDelete/Car` + deny `EditNewDelete/Car` loads and denies Edit, New and
   Delete while allowing Query and Read.
9. Anonymous callers still get 401 from access endpoints and the M-3 uniform 404 survives, with
   `AllowAllAccessControl` gone.
10. All four demos ship a `security.json`; DemoApp and WebhooksDemo demonstrate
    `Query`-without-`Read` on at least one type.
11. The existing test suites pass with the driver's default file; at least one test overrides it
    with inline JSON.
12. `--spark-verify-security` is wired into all four demos and a baseline is committed.

## Migration

Every app: author `App_Data/security.json` (or run the generator), delete
`AddAuthorization()`/`AllowAnonymousAccess()`, and keep the package reference only if it uses
identity. Namespaces change for `SecurityConfiguration`, `Right`,
`ISecurityConfigurationLoader` and `SparkAuthorizeAttribute`.

The package README documents a `security.json` shape the validator already rejects; it is
rewritten here.

## Out of scope

- **Attribute-level rights** (`Action/Type/Attribute`) and **schema-level grants** — real
  Vidyano capabilities, each its own issue.
- **Id-based group membership** (F8) — the name-matching weakness, more urgent after this lands.
- **`Machine:`/`Module:` prefix enforcement** (F8).
- **The `spark-sub-query` chrome work** (#309) — continues on `fix/parentless-sub-query`, minus
  `rowsNavigable`.
- **Vidyano's `IsImportant` override tier, `UserRight.Filter`, and writing the file back at
  runtime** — explicitly rejected. The first is a config-reachable third precedence tier; the
  second is a declarative control nothing enforces; the third makes a deployed artifact mutable.
