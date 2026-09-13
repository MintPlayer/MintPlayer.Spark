# Spark client — completing the conversation: implementation plan

**PRD:** [spark_client_conversation_PRD.md](spark_client_conversation_PRD.md)
**Status:** **M0, M1, M2, M2b and the S2/S4 spikes done** — 2184/2184 unit, 95/95 E2E, 39/39 client, 490/490 ng-spark, 278/278 generators. S3 dropped. The route table is fully literal, `OnLoad`/`OnQuery` have joined the retry mechanism, and both halves of a retry are centralised. The **server** side is finished; next are the client milestones M3–M8, which S1 gates.
**Branch:** `fix/datetimeoffset-fidelity` (shared with PR #403 at the issue owner's direction).

Method: red/green throughout, as `issue_384_plan.md` was. Every milestone that changes public API on
`MintPlayer.Spark.Client` lands its test first, and the RED must fail for the stated reason — a test
that fails because the method does not compile has proven nothing.

---

## Status

| Item | State |
|---|---|
| **S1** Does the client's wire traffic match the browser's for a full action→retry→resubmit flow? | **Not run.** Gates M3's API freeze. |
| **S2** What does a retry from `refresh` / `new` / `delete-row` actually return today? | **Done.** The exception **escapes the pipeline unhandled** — not a 449, not a loop, not a 500 from the endpoint. See below. |
| ~~**S3** How should a retry from `OnLoadAsync` / `OnQueryAsync` fail?~~ | **Dropped.** Superseded by the reads-become-POST decision — there is no failure to design, because those hooks stop being special. |
| **S4** Route shape, and how the type is authorized once it leaves the route | **Done.** Table settled (fully literal). Invariant pinned by `TypeConflationTests` and proven to fail when broken. Single-resolver design is M2 step 0. |
| **M0** Server: one retry seam instead of seven copies | **Done.** `IRetryableRequest` + `RetryAccessor.Accept` + `RetryScope`; five call sites, four downcasts removed. |
| **M1** Server: retry works from every hook that can prompt (FR7a/b) | **Done.** `new`, `delete-row` and `refresh` all emit and accept. ⚠️ Refresh needed a second fix — see below. |
| **M2** Server + clients: reads become POST, route table fully literal (FR21–FR25, FR29, FR30) | **Done.** 11 routes moved, both clients and 22 test files swept. See below. |
| **M2b** Server: centralise the emit half (FR26) | **Done.** One middleware catch replaces nine. ⚠️ One endpoint needs an exception filter — see below. |
| **M3** Client: answer a retry (FR1–FR6) | Not started |
| **M4** Client: surface and apply client operations (FR8–FR11) | Not started |
| **M5** Client: endpoint coverage (FR12–FR16) | Not started |
| **M6** Client: headers (FR18) | Not started |
| **M6b** Retire `SparkTestClient` (1 consumer left) | Not started. Gated on M5. |
| **M7** Version bumps + guard | Not started |
| **M8** Docs | Not started |

---

## S1 — Does the client speak the same wire as the browser?

**Question.** For one representative flow — execute a custom action that raises a retry, answer it,
receive the next response — is the client's HTTP traffic *equivalent* to what Angular sends? Same path,
verb, headers, and body shape.

**Why it matters.** The entire premise is *"if this SparkClient works in exactly the same way as the
Spark-angular-frontend, we can write tests without the frontend."* If the traffic diverges, the tests
are testing a second, parallel protocol implementation — which is worse than no test, because it passes
while the real client breaks. This is also what freezes the FR2 API shape before it is published.

**Method.**
1. Add a retry-raising custom action to `apps/Fleet` (one `CustomAction` calling
   `IRetryAccessor.Action("Are you sure?", ["Yes","No"])`, mirroring Vidyano's `AskFirst`).
2. Drive it in a real browser with the **`playwright_node` MCP**; capture every `/spark/**` request with
   `browser_network_requests` + `browser_network_request(includeRequestHeaders: true)`.
3. Drive the same flow through `SparkClient` with a request-logging `DelegatingHandler`.
4. Diff: path, verb, headers (`X-XSRF-TOKEN`, `X-Spark-Timezone`, `Content-Type`, `Cookie` presence),
   and the JSON body **by shape and by the `retryResults` array specifically**.

⚠️ **M2 dissolved two of those four axes, and split a third.** Re-scope before running it:

- **Path and verb are now identical by construction.** The route table is fully literal, so there is one
  literal string per operation on each side. A mismatch is a typo, not a protocol divergence — worth
  asserting, not worth a spike.
- **`X-XSRF-TOKEN` is no longer a "same or different" question.** On the four reads (`po/load`,
  `queries/get`, `queries/execute`, `actions/list`) the correct assertion is that **neither side sends
  one** — those endpoints carry no antiforgery metadata (`Get.cs:16-23`, R7). "Same headers" would pass
  on two clients that are both wrong.
- **The body shape is where all the remaining risk is**, and one divergence is already known and
  unnamed by this spike: `sortColumns` is a typed array on the wire (`QueryRequests.cs:22-24`), but
  `SparkClient.ExecuteQueryAsync` still takes the legacy `prop:asc` **string** and translates it in
  `ParseSortColumns` (`SparkClient.cs:276-291`). That translation exists on one client and not the
  other. It is deliberate and documented — but it is exactly the kind of thing S1 exists to turn into a
  decision rather than an accident, so it belongs in the divergence list.

The spike still gates M3's API freeze. What changed is that it is now a **body-shape** spike.

**Output.** A table of the two traces side by side, and an explicit list of every deliberate divergence
with its justification. Deliberate divergences are expected — the frontend sends `queryId` from grid
context, fetches the query catalogue first, and dispatches non-retry operations into UI services. The
spike's job is to make each one a *decision*.

**Why it is a spike and not a test.** The browser half needs a real Chromium and a running Fleet host;
pinning it as a test would add a second expensive browser test against a shared rate-limit budget
([R4](spark_client_conversation_PRD.md#r4--the-e2e-rate-limit-budget-is-shared)) to assert something
that only needs to be true once, at design time.

---

## S2 — What does a retry from `refresh` / `new` / `delete-row` return today?

**Question.** A hook calls `IRetryAccessor.Action(...)` from `OnRefreshAsync`, `OnNewAsync`,
`OnDeleteRowAsync`. What does the caller actually receive — 500, 449, a hang, an empty body?

**Why it matters.** It sizes M1 and it decides whether this is a bug fix or a behaviour change for
existing apps. ⚠️ **The PRD's first draft asserted an infinite loop and was wrong**, having reasoned from
the absence of a `catch` rather than measuring. Do not repeat that: run it.

**Method.** Three temporary hooks in `apps/Fleet`, one per endpoint, each raising a retry
unconditionally. Drive each with raw `HttpClient` (not `SparkClient`, which has its own 449 handling) and
record status, headers and body verbatim. Then drive the same three in a **browser** via
`playwright_node` and record what the user sees — a modal, an error toast, or nothing.

**Output.** A three-row table: endpoint → status → body → browser behaviour. Plus a yes/no on whether
any existing demo app or `apps/CodeCoverage` hook already raises a retry from these paths — if one does,
this is a production behaviour change, not a fix.

### Done. Measured 2026-09-12.

Run in-process via `SparkEndpointFactory` rather than the Fleet host — seconds instead of ~16 minutes,
against the same real route table, antiforgery gate and `security.json` enforcement. The spike is
committed as `tests/MintPlayer.Spark.Tests/Endpoints/PersistentObject/RetryFromEveryHookTests.cs` and
becomes M1's RED.

| Hook → endpoint | Result today |
|---|---|
| `OnBeforeSaveAsync` → `POST /po/{type}` | ✅ **449** with a well-formed `retry` operation |
| `OnBeforeDeleteAsync` → `DELETE /po/{type}/{id}` | ✅ **449** |
| `OnRefreshAsync` → `POST /po/{type}/refresh` | ❌ `SparkRetryActionException` **escapes the pipeline unhandled** |
| `OnNewAsync` → `POST /po/{type}/new` | ❌ same |
| `OnDeleteRowAsync` → `POST /po/{type}/delete-row` | ❌ same |

**The two passing rows are the control**, and they matter: they prove the fixture is sound, so the five
failures are the product and not the test. An earlier draft of this fixture failed all seven — wrong
`ClrType` (`AssemblyQualifiedName` where the loader wants `FullName`) and a missing required `name` on
the wire `PersistentObject`. Without a control, that would have read as "everything is broken".

**⚠️ Both earlier readings of this defect were wrong.**
- The PRD's first draft said *infinite loop* — reasoned from the missing `RetryResults` binding.
- The correction said *500 from the endpoint* — reasoned from the missing `catch`.

Measured, it is neither: nothing catches it anywhere, so it leaves the request pipeline as an unhandled
exception. What a real deployment returns then depends on the host's exception handling, which is
**not** part of this measurement — an in-process `TestServer` rethrows into the caller. ⚠️ **Confirm the
over-the-wire status against the Fleet host before claiming what a browser sees.**

**Checked, and clear: M1 is a fix, not a behaviour change.** The only retries anywhere in `apps/**` are
in `apps/Fleet/Fleet/Actions/CarActions.cs` — two in `OnBeforeSaveAsync` (`:99`, `:109`) and one in
`OnDeleteAsync` (`:133`). All three sit on paths that already emit 449 correctly. **`apps/CodeCoverage`
raises none at all**, so the production app is untouched by M1.

---

## S3 — How should a retry from a GET-backed hook fail?

**Question.** `OnLoadAsync` and `OnQueryAsync` run behind `GET` endpoints with no request body, so an
answer has nowhere to travel (**D3**). What should happen when a developer calls `Retry.Action(...)`
there?

**Why it matters.** FR7 says retries work from "pretty much all" hooks. These two are the exception, and
an unstated exception becomes a support question. Silence is the worst option: today it is an unhandled
exception with no explanation.

**Method.** Evaluate three options against the repo's existing machinery:
1. **Analyzer** — a `SPARK0NN` diagnostic when `IRetryAccessor` is used inside an `OnLoadAsync` /
   `OnQueryAsync` override. Precedent: `libs/source_generators/.../Diagnostics/`. ⚠️ Check whether the
   analyzer can see the enclosing override — `reference_analyzer_cross_assembly_location` records that
   analyzers cannot locate cross-assembly types, which may apply.
2. **Runtime throw** with an explanatory message from `RetryAccessor` when no answer channel exists.
3. **Support it properly** — a POST variant of load/query. Costed, almost certainly rejected.

**Output.** A recommendation with the rejected options and why.

---

## M0 — REFACTOR: one retry seam instead of seven copies

**No backward-compatibility constraint** (issue owner, 2026-09-12: the project is in preview), so this
is a straight refactor rather than an additive shim.

**Why this comes first, and is not cosmetic.** The two halves of a retry are copy-pasted per endpoint:

```csharp
// "accept" — verbatim in Create.cs:61, Update.cs:56, Delete.cs:43, Refresh.cs:85
if (request.RetryResults is { Length: > 0 } retryResults)
{
    var accessor = (RetryAccessor)retryAccessor;
    accessor.AnsweredResults = retryResults.ToDictionary(r => r.Step);
}

// "emit" — verbatim in Create.cs:103, Update.cs:96, Delete.cs:69, ExecuteCustomAction.cs:310
catch (SparkRetryActionException ex) { return ClientResult.Retry(clientAccessor, ex); }
```

**The defect in the PRD is what this duplication produces.** `Refresh` has the accept block and not the
`catch`. `New` and `DeleteRow` have neither. Nothing in the type system connects them, so a half-wired
endpoint compiles, ships, and fails only when a hook finally raises a retry. Implementing M1 by copying
the blocks three more times would leave **seven** copies and the same trap for the next endpoint.

**The refactor.**

1. `interface IRetryableRequest { RetryResult[]? RetryResults { get; } }` in
   `MintPlayer.Spark.Abstractions/Retry/`. Implemented by `PersistentObjectRequest`,
   `CustomActionRequest`, `RefreshPersistentObjectRequest`, and (M1) `NewPersistentObjectRequest`,
   `DeleteRowRequest`.
2. `RetryAccessor.Accept(RetryResult[]? results)` — owns the `ToDictionary`. Removes the
   `(RetryAccessor)retryAccessor` downcast that appears at all four sites.
3. A single `RetryScope.RunAsync(retryAccessor, clientAccessor, request, body)` doing **both** halves:
   accept before, catch after. Each endpoint wraps its existing body in it.

⚠️ Deliberately a helper, not an `IEndpointFilter`: the repo registers **no** endpoint filters today
(`grep IEndpointFilter libs/spark` → nothing), and inventing filter infrastructure to fix a duplication
problem is a larger, riskier change than the problem warrants.

**Verify:** all existing retry tests pass **unchanged** — `RetryActionDeleteTests`, plus whatever
`tests/MintPlayer.Spark.Tests` has for create/update/delete/action retries. This milestone must be
behaviour-neutral; M1 is where behaviour changes.

⚠️ **Verify the seam actually binds.** After M0, temporarily remove `IRetryableRequest` from one
request type and confirm a retry test fails. A seam that silently no-ops when a type forgets the
interface reproduces the original defect in a new costume.

---

## M1 — RED/GREEN: retry works from refresh, new and delete-row

**Gated on S2 and M0.** With M0 in place this is: implement `IRetryableRequest` on the two request
types that lack `RetryResults`, and wrap three endpoint bodies in `RetryScope.RunAsync`.

**RED.** `tests/MintPlayer.Spark.Tests/Endpoints/PersistentObject/RetryFromEveryHookTests.cs` — three
facts, one per endpoint, each asserting a **449** with a well-formed `RetryOperation` in `operations`,
and a fourth asserting the answer round-trips and the hook completes.

⚠️ **Verify RED for the right reason.** Each must fail with whatever S2 measured (likely 500), *not*
with a compile error or a 404 from a mis-typed route.

**GREEN.**
1. `NewPersistentObjectRequest` (`New.cs:247-264`) and `DeleteRowRequest` (`DeleteRow.cs:194-213`)
   implement `IRetryableRequest`.
2. `Refresh.cs`, `New.cs`, `DeleteRow.cs` — wrap the endpoint body in `RetryScope.RunAsync`.
3. Confirm `IRetryAccessor` is resolved **per request** in these three, as in the four that already
   work — it is registered `Scoped` (`RetryAccessor.cs:9`), and a singleton would leak one caller's
   answers into another's conversation.

**Verify:** the three new facts pass; the existing `RetryActionDeleteTests` still passes unchanged.

### Done. 2154/2154.

⚠️ **Refresh needed a second fix, in a different file, and the endpoint change alone did nothing.**
`RefreshInvoker` called `method.Invoke(actions, [args])` **without `BindingFlags.DoNotWrapExceptions`**.
`NewInvoker` and `DeleteRowInvoker` both pass it, with a comment calling it load-bearing; this one never
did. So every exception from an `OnRefresh` hook arrived wrapped in `TargetInvocationException` and no
typed `catch` in the endpoint could match it.

That is wider than retry: a refresh hook throwing `SparkValidationException` to refuse politely would
have surfaced as a 500 with the hook's stack trace lost. Fixed to match its siblings.

**The diagnostic that mattered:** `new` and `delete-row` went green on the endpoint change alone while
`refresh` stayed red after the *identical* change — which is what pointed at the invoker rather than the
endpoint.

⚠️ **Fixture naming:** `ActionsResolver` matches an actions class to an entity by **simple name across
the whole assembly**, and nesting does not scope it. The spike's nested `OrderActions` claimed to be the
actions class for another fixture's nested `Order` and broke seven of its tests. Fixture entities need a
name no other fixture would pick — hence `RetryProbe`.

**Retry from `OnRefreshAsync` is supported, deliberately.** It was nearly cut on the reasoning that
"No" has no meaning for a refresh. That reasoning was wrong: the hook is **re-entered** with
`Retry.Result` populated and decides the outcome itself, so the refresh completes either way and "No"
is a branch in the hook rather than an aborted request. The motivating case is a dropdown warning —
*"setting Status to Expired makes this read-only forever, are you sure?"*.

---

## S4 — Route shape once reads are POST

**Question.** What should the route table look like, given that REST compliance is already gone?

**Why it matters.** `POST /{objectTypeId}/{**id}` would sit beside `POST /{objectTypeId}` (create),
`.../new`, `.../refresh` and `.../delete-row`. ASP.NET prefers literal segments, so those four win —
which means **an object whose id is literally `new` or `refresh` becomes unreachable**. Ids are
application-supplied, and `refresh` is a plausible slug.

**The insight that dissolves it.** The catch-all exists because **Raven ids contain slashes**
(`cars/1-A`), not because of the verb. Ids are the only user-supplied path data that can contain a
separator. Move the id into the body and the catch-all disappears, and with it every ambiguity — while
the *type* stays in the path, where it is worth keeping for logs and traces.

### Recommended scheme — fully literal paths

The issue owner's position, 2026-09-12: *"It doesn't necessarily need to be REST compliant, it's no
longer REST compliant anyway now"*, and the target is **absolute zero chance of collisions**.

⚠️ **Measured while drafting this: nothing validates the character set of a type name or alias.** There
is no regex, no rejection of separators — `SparkQueryAliases` checks only for *duplicate* aliases
(`:49`). So any scheme with a type in the path is collision-free by **convention**, not by
construction: one alias containing a `/` reintroduces the whole problem. That rules out the
intermediate design.

**Every path becomes fully literal — no route variables at all.**

| Today | Proposed | Body carries |
|---|---|---|
| `GET /po/{type}/{**id}` | `POST /po/load` | `objectTypeId`, `id` |
| `POST /po/{type}` | `POST /po/create` | `objectTypeId`, `persistentObject` |
| `PUT /po/{type}/{**id}` | `POST /po/update` | `objectTypeId`, `persistentObject` |
| `DELETE /po/{type}/{**id}` | `POST /po/delete` | `objectTypeId`, `id` |
| `POST /po/{type}/new` | `POST /po/new` | `objectTypeId`, `asDetailAttribute`, `parentType`, `parentId` |
| `POST /po/{type}/refresh` | `POST /po/refresh` | `objectTypeId`, `persistentObject`, `triggeredBy` |
| `POST /po/{type}/delete-row` | `POST /po/delete-row` | `objectTypeId`, `asDetailAttribute`, `parentId`, `rowKey` |
| `GET /queries/{id}/execute` | `POST /queries/execute` | `queryId`, paging, sort, **filters** |
| `GET /queries/{id}/` | `POST /queries/get` | `queryId` |
| `POST /actions/{type}/{name}` | `POST /actions/execute` | `objectTypeId`, `actionName`, … |

**Zero variables in the route table means zero collisions, permanently and by construction** — not
"provided nobody names a type badly". It also removes the last catch-all, so an id containing slashes
stops being a routing concern at all.

**What is given up, and how much it costs.** Request logs no longer name the *type* — `POST /spark/po/load`
covers every load. They still name the **operation**, which is what latency and error dashboards group
by, and the type is one structured-log field away (it is in the body the endpoint already
deserialises). ⚠️ Anything today that groups by URL *path* to distinguish types will stop doing so;
worth a check against whatever observability the deployment runs before this lands.

**Create and update stay separate verbs**, deliberately. `Create.cs:71-78` records a security fix
(R2-M18): POST is Create and forces `Id = null`, because a client posting an existing id used to flip
the operation to Edit and overwrite a foreign record under the New right. Collapsing both into one
`save` verb reintroduces exactly that.

**Breaking changes are in scope** — issue owner, 2026-09-12: *"Libraries still in preview, so these
breaking changes are allowed."* So the old routes are deleted outright rather than aliased.

**Create and update stay separate verbs**, deliberately. `Create.cs:71-78` records a security fix
(R2-M18): POST is Create and forces `Id = null`, because a client posting an existing id used to flip
the operation to Edit and overwrite a foreign record under the New right. Collapsing both into one
`save` verb reintroduces exactly that.

**Method.** With a fully literal table there is no ambiguity left to probe for — the spike's remaining
job is the two things that *can* still go wrong:
1. **Authorization still keys off the right thing.** Every endpoint resolves the entity type from the
   route today. Moving it to the body must not weaken the rule that the type used for the permission
   check is the one the *server* resolved, never one the client asserted — see `Refresh.cs:100-106`
   ("taking the client's word for the type is how a caller reads one collection through another's
   permissions", security sweep C3). ⚠️ **This is the real risk in the migration**, and it is larger
   than the collision it fixes: the route was previously an independent statement of intent, and now
   the body is the only source.
2. **Antiforgery metadata survives.** `RequireAntiforgeryTokenAttribute(true)` is per-endpoint
   metadata; confirm each new route carries it.

**Output.** A checked route table, plus an explicit statement of how the type is resolved and
authorized now that it arrives in the body.

### Done. 2026-09-12.

**The invariant is pinned and proven, in `TypeConflationTests` (3 facts).** A payload claiming a
different type must never change which type is used, and must never buy access to a denied one. Written
against the **current** routes on purpose: it passes today and must still pass after M2. If someone
wires authorization to the nested field, that is what fails.

⚠️ **Proven to discriminate, not assumed.** `Create` was temporarily mutated to resolve the type from
the payload; both `Create` facts failed, including the one where trusting the payload lets a caller
write to a type `security.json` denies. Mutation reverted, 10/10 green. A security test that cannot
fail is worse than none — and this session already produced one vacuous fixture that looked fine.

**Considered and rejected: drop the top-level field and use `persistentObject.objectTypeId` as the
single source.** The instinct is right — one source removes the chance of checking one field and using
the other — but that field cannot be it:

| operation | carries a `PersistentObject`? |
|---|---|
| `create`, `update`, `refresh` | yes |
| `load`, `delete`, `new`, `delete-row`, `queries/execute` | **no** |

**Five of nine have no object to take a type from.** `NewPersistentObjectRequest` and
`DeleteRowRequest` carry `AsDetailAttribute`/`ParentType`/`ParentId`; `load` and `delete` carry an id.

And a second reason that holds even where an object *is* present: `persistentObject.objectTypeId` is
**submitted data**, not a request parameter — the object declaring what it is. Authorizing a write
against the written object's own self-declaration is the confused-deputy shape, which is precisely what
`Create.cs:64` overwrites it to prevent.

### The resolution — one reachable source (M2 step 0)

Keep the goal, reach it differently: make the nested field **unreachable** rather than authoritative.

```csharp
// The one place in the codebase that answers "which type is this request about".
internal static class SparkRequestType
{
    public static EntityTypeDefinition? Resolve(IModelLoader modelLoader, ISparkTypedRequest request);
}
```

- `ISparkTypedRequest { string? ObjectTypeId { get; } }` on every request body, alongside
  `IRetryableRequest`. Same pattern as the M0 seam, for the same reason.
- Every endpoint calls it; **no endpoint reads `RouteValues["objectTypeId"]` or
  `persistentObject.ObjectTypeId`** afterwards.
- One line to review, one line to get wrong, and `TypeConflationTests` fails the moment it reads the
  nested field.

⚠️ The nested `ObjectTypeId` stays on the **response** — clients need it, and nested AsDetail objects
carry their own. It is inbound use that is being designed out. Continue overwriting it with the
resolved type on the way in (`Create.cs:64`, `Update.cs:59`) so a response never echoes a client's
claim back as fact.

⚠️ **Scope.** This is now a full route-table migration rather than a verb change on two endpoints:
every `ng-spark` service method, every `SparkClient` method, and every test that issues HTTP. It is
worth doing in one pass precisely because it is all the same edit — but it should not be smuggled in as
a sub-step of a retry milestone, which is why it is M2 and not part of M1.

---

## M2 — RED/GREEN: reads become POST

**Gated on S4.** The largest milestone here; see the PRD's *Reads become POST* for why it is worth it
independently of retry.

**RED.**
1. `OnLoadAsync` and `OnQueryAsync` rows added to `RetryFromEveryHookTests` — a retry from each must
   produce a 449 and accept its answer. These are the facts that only a body can satisfy.
2. A test asserting the read endpoints answer on `POST` and **no longer** on `GET`.

**GREEN, in order.**
0. **`SparkRequestType.Resolve` + `ISparkTypedRequest` first** (see S4). Land it against the *current*
   routes so `TypeConflationTests` proves it behaves identically before anything moves — a refactor and
   a migration in one step means a failure in either is indistinguishable from a failure in the other.
1. Server: `Get.cs` and `ExecuteQuery.cs` become `POST` endpoints with typed request bodies
   implementing `IRetryableRequest`. Query parameters move from the query string into the body —
   `skip`, `take`, `search`, `sortColumns`, `parentId`, `parentType` — typed rather than string-parsed.
   ⚠️ `sortColumns` is `prop:asc|desc` comma-joined on the wire today (`spark.service.ts:114-116`);
   in a body it becomes a real array, and the string form goes away rather than being kept as an alias.
2. Both endpoints wrap their hook invocation in the retry `catch`, via the M0 seam.
3. `ng-spark`: `SparkService.get` and `.executeQuery` post. ⚠️ Both must route through
   `sendWithEnvelope`, or the read paths gain a retry the client cannot answer — the exact trap that
   makes M2b unsafe before this lands.
4. `MintPlayer.Spark.Client`: the same two methods, and `requiresAntiforgery: true` on both — a POST
   goes through the antiforgery gate.
5. Sweep every caller: `tests/MintPlayer.Spark.E2E.Tests` (several classes issue bare `GET`s),
   `tests/MintPlayer.Spark.Tests`, and the demo apps.

⚠️ **Delete the `GET` routes; do not leave them as aliases.** No backward-compatibility requirement
applies (preview), and an alias hides exactly the callers the sweep needs to find. A missed caller
should 404 immediately rather than work until someone removes the alias later.

**Verify:** `RetryFromEveryHookTests` covers all seven hooks; full unit suite; full E2E suite.

### Done. 2026-09-12. 2159/2159 unit, 39/39 client, 490/490 ng-spark.

**Eleven routes**, not the two FR21/FR22 named. The two catalogue reads (`GET /spark/queries/{id}`,
`GET /spark/actions/{type}`) were not in the original table and had to move too, or "zero route
variables" would have been "zero variables on the endpoints we remembered". Recorded as FR29.

**The order that mattered: step 0 landed as its own commit** (`cab6a0fa`), against the *old* routes.
`SparkRequestType.Resolve` replaced nine copies of the same two lines while the route was still the
source, `TypeConflationTests` proved the behaviour identical, and only then did the source move. A
refactor and a migration in one commit would have left a failure in either indistinguishable from a
failure in the other — and this is the security-relevant one.

**The guard has two halves, and the second was added here.**
`SparkRequestTypeSingleSourceTests` scans the endpoint sources: no endpoint reads the route value, and
no `ObjectTypeId` reaches a type lookup. Both facts were **verified to go red** under a deliberate
mutation of `Create`, each naming the offending file and line.

⚠️ Its second fact deliberately forbids *"ObjectTypeId reaches a type lookup"* rather than the broader
*"ObjectTypeId is read"*. The broad version was written first and was wrong: `Get.cs:95` compares
`obj.ObjectTypeId` against a client-operation target on an object the **server** built from the
database. That is a legitimate read, and a rule that bans it only teaches people to work around the
rule.

**An ordering inversion the migration forced, and why the property survives it.** The body must now be
read before anything can be authorized, because the body is where the type is. `Create` previously
checked the type-level "New" right *first*, so that POSTing rubbish could not tell an unauthorized
caller which types exist (N23). `SparkRequestType.ReadAsync` preserves the property by answering a
malformed body **exactly as it answers an unknown type** — both null, both refused identically — so a
parse failure reveals only that the JSON was bad.

**Two things deliberately not done, recorded as FR30.** The read endpoints carry no antiforgery
metadata (the verb changed; what they do did not — see R7, which was resolved by declining its
premise), and their success responses stay bare objects. Only the 449 is enveloped.

**What the sweep cost, and what it caught.** 22 test files, both clients, 90-odd call sites. A
balanced-paren rewriter handled the repetitive `new`/`refresh`/`delete-row` sites; the seven it wrapped
wrongly all failed at **compile time**, which is the outcome R6 hoped for and did not expect. Two
fixtures needed more than a call-site edit:

- `DenyAllEndpointMirrorTests` — its rows now carry **bodies naming real targets**. ⚠️ Without them the
  suite would have gone green vacuously: an endpoint given an empty body refuses because it cannot tell
  what was asked, and that refusal is the same 404 the test asserts. The rows would have passed without
  ever reaching the authorization they exist to check.
- `ScriptedHttpHandler` — now captures request **bodies**. It recorded only URLs, which was enough when
  the URL was the request; a test asserting on the body got a `NullReferenceException` from a drained
  content stream, not an empty string.

`Wire.Typed/Query/Action` live in `MintPlayer.Spark.Testing` rather than in one test project, so every
suite builds these bodies the same way — and so that `objectTypeId` lands at the top level rather than
inside `persistentObject` by accident.

---

## M2b — REFACTOR: centralise the emit half

**⚠️ Strictly after M2 — which is now satisfied, so this is unblocked.** Not an ordering preference — doing it first converts a *loud* failure on the
read paths into a *silent* one, because a central `catch` would return a well-formed 449 to a client
with no way to answer it.

M0 unified the **accept** half. The **emit** half is still a `catch` clause in **nine** endpoints now —
M2 added `load` and `queries/execute` to the seven, which is two more copies of the same three lines and
makes the case for this stronger, not weaker.

✅ **The `OnLoadAsync` / `OnQueryAsync` rows are in** (11 facts), added immediately after M2 rather than
deferred — without them both endpoints were exactly the shape M0 exists to prevent: wired, looking
finished, proved by nothing.

⚠️ **Writing them found a third `DoNotWrapExceptions` omission, and a fourth.** `DatabaseAccess` has
five `MethodBase.Invoke` sites and `QueryExecutor` one; none passed the flag that `NewInvoker`,
`DeleteRowInvoker` and `RefreshInvoker` all do. Both are fixed.

**Why it survived this long, and what it means for hook authors.** The flag only bites a hook that
throws *before* its `Task` exists. An `async` override's exception lands on the returned task and
`await` rethrows it unwrapped — so the ordinary case looked fine, and it was a **non-async override
refusing up front** that arrived as `TargetInvocationException` with no typed `catch` matching. That is
the shape of a validation guard, which makes it the shape most likely to be written.

⚠️ **And a real warning the fixture had to be restructured around:** `OnLoadAsync` is not the load
endpoint's hook — it is *the* load seam, so delete, refresh and delete-row all run it on their way
elsewhere. Prompting there made five unrelated rows fail with `"Load?"`. A retry in `OnLoadAsync` fires
on **every** read of that type. The probe needed its own entity (`RetryProbeRead`) to keep the prompt
where the test could see it.

1. Catch it in `SparkMiddleware` around `await next(context)` — there is no global exception handler
   there today, so this is new machinery rather than an extension of existing machinery.
2. `ClientResult.Retry` needs `IClientAccessor`, which is request-scoped and resolvable at that point.
3. Remove the nine per-endpoint `catch` clauses.

⚠️ **A central catch only sees what reaches it unwrapped.** Three `DoNotWrapExceptions` omissions have
now been found on three separate paths, each hiding a hook's exception behind
`TargetInvocationException`. Before removing the per-endpoint catches, check every reflective hook
invocation passes the flag — a central handler that silently fails to match is strictly worse than nine
that do.

**Verify:** `RetryFromEveryHookTests` unchanged and still green — it is the enforcement, and it should
not need editing for a refactor that changes only where the exception is caught.

### Done. 2026-09-12.

The prediction above held for the eleven existing rows: they passed untouched. What it missed is that
the matrix had a **hole**, and centralising is what exposed it.

**⚠️ One endpoint needs a filter, and it was the one endpoint the matrix did not cover.**
`ExecuteCustomAction` has a catch-all (R2-M1: log the detail, return a generic 500). Every other
retry-capable endpoint catches only specific types, so removing its `catch` lets the exception
propagate — but there the catch-all swallowed the prompt and answered **500 "Operation failed"**, with
the retry operation still sitting unused in the envelope. `when (ex is not SparkRetryActionException)`
is the fix, and `Custom_action_emits_a_retry` is the row that now fails without it. Both verified by
mutation.

**⚠️ The emit half is no longer testable at the endpoint level, and one test had to move.**
`ExecuteCustomActionTests` constructs the endpoint and calls `HandleAsync` directly, so no middleware
is in the picture. Its 449 assertion became untrue the moment the conversion moved, and could only be
made to pass by putting the `catch` back. It now asserts what the endpoint actually does — the
exception escapes, payload intact — and the 449 is asserted end-to-end by the matrix. That is the same
property stated where it is true, not a weaker one.

**Also fixed here, because M2b depends on it:** eight more reflective invokes into application code
were missing `DoNotWrapExceptions` (`QueryExecutor` ×3, `RowSecurity` ×3, `ReferenceResolver`,
`StreamingQueryExecutor`, `SyncActionHandler` ×2). All now go through `SparkHookInvocation.HookInvoke`,
and `HookInvocationTests` fails if a new call site forgets it — verified by mutation, naming file and
line.

⚠️ **`MintPlayer.Spark.Messaging` is deliberately excluded.** Its `MessageProcessor` catches
`TargetInvocationException` explicitly and unwraps `InnerException`, including a separate
non-retryable clause keyed on it. The rule is "a typed catch must be able to match"; there, one can.
Applying the flag mechanically would have made working code dead.

**Kept:** `ClientResult.Retry` still falls back to building the operation from the exception's own
fields when the accessor has none, so a hook that throws `SparkRetryActionException` directly — without
going through `IRetryAccessor` — still produces a well-formed prompt. The middleware changed where that
runs, not what it does.

---

## M3 — RED/GREEN: the client can answer a retry

**Gated on S1** — the API is published, so it is frozen only once the wire is confirmed.

**RED.** `tests/MintPlayer.Spark.Client.Tests/RetryConversationTests.cs`, driving the in-process
`SparkEndpointFactory` host (not the Fleet E2E host — no need for a `dotnet run` subprocess here):
- a single retry: execute → `IsRetry` → set an attribute → `ContinueAsync("Yes")` → completed
- a **repeated** retry: two prompts, asserting the second request carries **both** answers (FR3)
- the cap: a hook that always re-raises throws after N rather than spinning (FR5)
- an option not in `options` is rejected client-side (FR6)

**GREEN.**
1. `SparkActionResult` gains what a continuation needs — the originating request, so `ContinueAsync`
   can rebuild it. ⚠️ Keep the conversation in the **request**, not on `SparkClient`: two concurrent
   conversations must not interfere (the one structural idea worth taking from `Vidyano.Core`).
2. `SparkClient.ContinueAsync(SparkActionResult, string option, CancellationToken)`.
3. `RetryResult[]` threaded onto **every** retry-capable request body: create, update, delete, **load**,
   refresh, **new**, **delete-row**, action **and `queries/execute`** — nine, not five.

   ⚠️ This list read "create/update/delete/refresh/action" and was short by four. M2 made every endpoint
   retry-capable and the server DTOs prove it: `PersistentObjectReferenceRequest` — the body of *both*
   `po/load` and `po/delete` — implements `IRetryableRequest` (`PersistentObjectRequest.cs:36,45`), and
   so does `ExecuteQueryRequest` (`QueryRequests.cs:27,48`). Shipping the short list would leave the
   client unable to answer a retry raised from `OnLoadAsync` or `OnQueryAsync` — which is precisely the
   unlock that "reads become POST" was bought for.

   ⚠️ **This step's original instruction was inverted by M2 and must not be followed as it stood.** It
   read: *"`DeletePersistentObjectAsync` currently sends no body at all (`SparkClient.cs:205-213`); the
   frontend attaches one only once `retryResults` is non-empty (`spark.service.ts:317-323`). Match that
   exactly — sending an empty body unconditionally is a wire change S1 would flag."*

   **Both halves of that are now false.** A delete is `POST /spark/po/delete` and **always** carries
   `{ objectTypeId, id }` — in the client (`SparkClient.cs:211-217`) and in the frontend
   (`spark.service.ts:231-236`) alike. The conditional body is gone from both, along with the
   `Content-Type` sniffing the server used to do to detect it.

   So the instruction now points the wrong way: following it would make the client send *less* than the
   frontend and **cause** the divergence S1 exists to catch. The correct step is the ordinary one —
   add `retryResults` to a body that is already there.
4. `RetryAnswer` + the `onRetry:` convenience overload (FR4), implemented **over** `ContinueAsync`.
5. `MaxRetryDepth` with a documented default.

**Verify:** all four facts; `nx run-many --target=test` green.

---

## M4 — RED/GREEN: client operations are surfaced and applied

**RED.** Facts that a `notify` is visible to the caller, that a `refreshAttribute` has been applied to
the in-memory `PersistentObject` (including the `object`/`objects` AsDetail payloads, which carry the
value because `value` is null for those), that an unknown `type` is ignored rather than thrown (FR10),
and that non-retry operations are observable **before** the retry prompt (FR11).

**GREEN.** Read `operations` in `ReadEnvelopeResultAsync<T>` (`SparkClient.cs:442-449`) instead of
discarding; expose them on the result types; apply the two that matter. ⚠️ This changes the meaning of
existing return values — check `docs/prd/PRD-ClientOperations.md` for decisions already taken before
designing this.

---

## M5 — GREEN: endpoint coverage

Typed methods for `refresh`, `new`, `delete-row`, `/spark/lookupref/*` (5), `GET /spark/types/{id}`,
`GET /spark/actions/{type}`, `/spark/program-units`, `/culture`, `/translations`; `queryId` on
`ExecuteActionAsync` (FR15); read the action response envelope (FR16).

Each gets one round-trip fact. ⚠️ `lookupref` add/update/delete are **not enveloped**
(`spark.service.ts:276-287`) — do not route them through the envelope reader.

---

## M6 — GREEN: headers

`X-Spark-Timezone` and `Accept-Language` as client options, defaulting to unset. ⚠️ `HttpClient` sends
neither by default and the server silently falls back, so a test asserting culture or timezone behaviour
through the client is asserting the fallback unless this is set.

---

## M6b — REFACTOR: retire `SparkTestClient`

**No backward-compatibility constraint**, and this one removes a documented source of confusion — the
issue owner's own first reading was *"the workspace already seems to contain a `SparkTestClient.cs` but
that seems to be something different."* It is: a 59-line untyped CSRF shim over `TestServer`
(`libs/testing/MintPlayer.Spark.Testing/SparkTestClient.cs`), returning raw `HttpResponseMessage` and
knowing nothing about envelopes, retry or `PersistentObject`.

It has **one consumer left** —
`tests/MintPlayer.Spark.Tests/Endpoints/LookupReferences/LookupReferenceEndpointTests.cs` — and it
exists only because `SparkClient` had no `lookupref` methods. **M5 removes that reason.**

1. Migrate that test onto `SparkClient` + the M5 lookupref methods.
2. Delete `SparkTestClient.cs` and `SparkEndpointFactoryExtensions.CreateAuthorizedClientAsync`.
3. Keep `SparkEndpointFactory.MintAntiforgeryAsync()` — it is used independently.

⚠️ `SparkTestClient` replays a cookie/token minted **once** and cannot re-prime after a login, where
`SparkClient.InvalidateAntiforgery()` can. Check the migrated test does not depend on the stale-token
behaviour before deleting.

⚠️ It ships in the packable `MintPlayer.Spark.Testing` package, so this is a **public API removal** —
fine in preview, but it belongs in the release notes rather than passing silently.

---

## M7 — version bumps, and a guard

All 24 `libs/**/*.csproj`: `10.0.0-preview.NN` → `NN+1`, in lockstep — CI fails any `libs/**` change
without a bump (`pull-request.yml:172-207`), and `dotnet pack` is solution-wide so a partial bump
publishes inconsistent versions. No npm change unless M4 touches `ng-spark`.

⚠️ Major digit stays `10` — it tracks `net10.0`, not our API. A break inside a .NET generation is a
preview bump.

⚠️ **M2 makes this release breaking, and `ng-spark` moves too.** Every published route changed, so
every consumer of `@mintplayer/ng-spark` or `MintPlayer.Spark.Client` has to take both halves together —
an old client against a new server 404s on every call. The npm **major** still does not move (it tracks
Angular, not our API); this is a minor with the break in the release notes.

⚠️ `MintPlayer.Spark.Testing` gained public API (`Wire`), and M6b removes some (`SparkTestClient`). Both
belong in the notes rather than passing silently.

---

## M8 — docs

- `libs/client/MintPlayer.Spark.Client/README.md` (create if absent) — the conversation loop with the
  worked example from the PRD.
- `docs/guide-*.md` — a short section on retry from hook code. ⚠️ Its shape changed: there is no longer
  a set of hooks that "cannot have it". All nine can. What the section needs instead is the **route
  table** (every path is literal, every call is a POST, parameters go in the body) and the
  `OnLoadAsync` warning — a retry there fires on every read of the type, including the loads that
  delete, refresh and delete-row perform on their way elsewhere.
- Update this plan's Status table and record S1's divergence list as the standing answer to *"does the
  client match the frontend?"*

✅ **The route-table half is already done, in the M2 commit rather than deferred here.** Leaving the
guides documenting deleted routes would have been shipping a known break in the documentation. Updated:
`guide-custom-actions`, `guide-queries-and-sorting` (its "runtime sort override" section documented
`?sortBy=`/`?sortDirection=`, which had *already* been replaced by `sortColumns` before this work),
`guide-search`, `guide-aliases`, `guide-authorization`, `guide-asdetail-attributes`,
`guide-triggers-refresh`, plus the endpoint table in `MintPlayer.Spark/README.md` and the two
`MintPlayer.Spark.Testing` documents.

⚠️ **Historical PRDs, plans and build logs under `docs/` were deliberately left alone.** They are
records of what was decided when, and rewriting the routes inside them would make them lie about their
own moment. Only live developer documentation was updated. The same reasoning applies to the code
comments that describe the old shape on purpose — several of them are *why* the new shape exists.

---

## Deliberately not doing

- **Migrating the 7 raw-HTTP E2E classes.** Enabled here, done separately. Landing a public API change
  and a large test rewrite together makes a failure in either indistinguishable.
- **Replacing the 4 browser-bound test classes.** They assert rendering, redirects and transport
  behaviour no request/response client can see.
- **A scripting DSL.** C# API only.
- **WebSocket streaming client.** No test needs it; unused public API is a liability.
- **External login.** Browser-dependent by construction.
