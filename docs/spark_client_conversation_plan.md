# Spark client — completing the conversation: implementation plan

**PRD:** [spark_client_conversation_PRD.md](spark_client_conversation_PRD.md)
**Status:** **M0 and M1 done** (2154/2154). S2 resolved. S3 dropped. **S4 and M2 next** — reads become POST, which is what lets `OnLoad`/`OnQuery` join the retry mechanism and makes M2b safe.
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
| **S4** Route shape for the POST read: `POST /{type}/{**id}` or `POST /{type}/load`? | **Not run.** Gates M2. |
| **M0** Server: one retry seam instead of seven copies | **Done.** `IRetryableRequest` + `RetryAccessor.Accept` + `RetryScope`; five call sites, four downcasts removed. |
| **M1** Server: retry works from every hook that can prompt (FR7a/b) | **Done.** `new`, `delete-row` and `refresh` all emit and accept. ⚠️ Refresh needed a second fix — see below. |
| **M2** Server + clients: reads become POST (FR21–FR25) | Not started. Gated on S4. |
| **M2b** Server: centralise the emit half (FR26) | Not started. ⚠️ **Strictly after M2.** |
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

---

## M2b — REFACTOR: centralise the emit half

**⚠️ Strictly after M2.** Not an ordering preference — doing it first converts a *loud* failure on the
read paths into a *silent* one, because a central `catch` would return a well-formed 449 to a client
with no way to answer it.

M0 unified the **accept** half. The **emit** half is still a `catch` clause in seven endpoints. Once
every endpoint can accept an answer, one handler can convert `SparkRetryActionException` into a 449
envelope for all of them.

1. Catch it in `SparkMiddleware` around `await next(context)` — there is no global exception handler
   there today, so this is new machinery rather than an extension of existing machinery.
2. `ClientResult.Retry` needs `IClientAccessor`, which is request-scoped and resolvable at that point.
3. Remove the seven per-endpoint `catch` clauses.

**Verify:** `RetryFromEveryHookTests` unchanged and still green — it is the enforcement, and it should
not need editing for a refactor that changes only where the exception is caught.

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
3. `RetryResult[]` threaded onto the create/update/delete/refresh/action request bodies.
   ⚠️ `DeletePersistentObjectAsync` currently sends **no body at all** (`SparkClient.cs:205-213`); the
   frontend attaches one only once `retryResults` is non-empty (`spark.service.ts:317-323`). Match that
   exactly — sending an empty body unconditionally is a wire change S1 would flag.
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
that seems to be something different."* It is: a 44-line untyped CSRF shim over `TestServer`
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

---

## M8 — docs

- `libs/client/MintPlayer.Spark.Client/README.md` (create if absent) — the conversation loop with the
  worked example from the PRD.
- `docs/guide-*.md` — a short section on retry from hook code, naming the three hooks that gained it and
  the two that cannot have it.
- Update this plan's Status table and record S1's divergence list as the standing answer to *"does the
  client match the frontend?"*

---

## Deliberately not doing

- **Migrating the 7 raw-HTTP E2E classes.** Enabled here, done separately. Landing a public API change
  and a large test rewrite together makes a failure in either indistinguishable.
- **Replacing the 4 browser-bound test classes.** They assert rendering, redirects and transport
  behaviour no request/response client can see.
- **A scripting DSL.** C# API only.
- **WebSocket streaming client.** No test needs it; unused public API is a liability.
- **External login.** Browser-dependent by construction.
