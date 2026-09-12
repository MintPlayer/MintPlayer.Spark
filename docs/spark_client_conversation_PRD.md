# Spark client — completing the conversation

**Status:** **Proposed.** Not started. Investigation complete (4 parallel surveys, 2026-09-12); every claim
below is cited to code.
**Plan:** [spark_client_conversation_plan.md](spark_client_conversation_plan.md)

---

## Executive summary

| Belief | Finding |
|---|---|
| *"We need a Vidyano-style `Client.cs` for Spark."* | **Half-refuted.** `MintPlayer.Spark.Client` already ships one — typed CRUD, queries, metadata, permissions, actions, auth, antiforgery, cookie jar, `CancellationToken` throughout. |
| *"So this is a new library."* | **Refuted.** It is completing an existing published package. A new package id would be published to nuget.org permanently on first merge. |
| *"The retry loop needs building from scratch."* | **Half-refuted.** The client already *hears* a retry (449 → `RetryActionPayload`). It cannot *answer* one. |
| *"`SparkTestClient` is the thing to extend."* | **Refuted.** It is a 44-line CSRF shim over `TestServer` with one consumer left. Unrelated. |
| *"A protocol client can replace the browser tests."* | **Refuted.** Measured this session: the two defects found in `ViewerTimezoneRenderingTests`' area were a blank `datetime-local` and `DatePipe` zone conversion. A protocol client is green through both. |

**The one-line statement of work:** the client can hear a retry and cannot answer one; close that, and
the ~10 endpoints with no typed method, so a test can drive the same conversation the Angular frontend
drives.

---

## Why

`tests/MintPlayer.Spark.E2E.Tests` has **28 test classes**. Seven of them hand-build raw HTTP because
`SparkClient` cannot express what they need:

| Class | What it hand-rolls |
|---|---|
| `Retry/RetryActionDeleteTests` | the retry resubmit — with a comment saying so (`:17-18`) |
| `Refresh/TriggersRefreshTests` | `POST /spark/po/{type}/refresh` (8 raw calls) |
| `RowLifecycle/ServerSideRowLifecycleTests` | `new` / `delete-row` (19 raw calls, 409 lines) |
| `Security/LookupReferenceAuthTests` | all of `/spark/lookupref/*` |
| `Security/CrossModuleSyncTests`, `JwtBearerCredentialTests`, `ModuleCertificateCredentialTests` | module/credential endpoints |

Each hand-rolled body is a second, untested copy of the wire format. When the format moves, they rot
silently — a JSON property that stops being read produces a passing test and a broken feature, which is
[exactly the defect class](raven_datetimeoffset_and_sort_companions_summary.md) this repo has hit three
times recently.

The second reason is reach: a multi-step flow through the browser costs ~15 minutes of Fleet host boot
and a large slice of a shared rate-limit budget
(see [the plan's M-note on the shared bucket](#r4--the-e2e-rate-limit-budget-is-shared)). The same flow
through the client costs milliseconds.

---

## What exists today

`libs/client/MintPlayer.Spark.Client` — **shipped NuGet package**, `PackageId` `MintPlayer.Spark.Client`,
`Version` `10.0.0-preview.81` (`MintPlayer.Spark.Client.csproj:10-11`). **Zero package dependencies** —
one `ProjectReference` to `MintPlayer.Spark.Abstractions` (`:23`). Four source files. Referenced by the
three test projects; **no app references it**.

### Covered

`GetPersistentObjectAsync` ×2, `Create/Update/DeletePersistentObjectAsync`, `ExecuteQueryAsync` ×2,
`GetQueryAsync` ×2, `ListQueriesAsync`, `ListEntityTypesAsync`, `ListAliasesAsync`,
`GetPermissionsAsync`, `ExecuteActionAsync`, plus `SendAsync` / `InvalidateAntiforgery` as public
primitives. `Login/Register/Logout/GetCurrentUserAsync` come from the sibling package
`MintPlayer.Spark.Client.Authorization`.

Antiforgery is already correct: warm up, read the `XSRF-TOKEN` cookie, echo `X-XSRF-TOKEN`
(`SparkClient.cs:454-480`), matching the server's double-submit
(`SparkMiddleware.cs:50`, `:264-275`).

### Not covered

| Gap | Evidence |
|---|---|
| **Answering a retry** — no method takes `RetryResult[]` | the string `retryResults` appears nowhere under `libs/client/`; server accepts it on action (`CustomActionRequest.cs:47`), create (`Create.cs:61`), update (`Update.cs:56`), delete (`Delete.cs:43`), refresh (`Refresh.cs:85`) |
| **Client operations** — discarded | `SparkClient.cs:437-449`, *"Operations are currently discarded"* |
| **The action response body** — discarded entirely | `:388` returns `SparkActionResult.ForSuccess((int)response.StatusCode)`; the envelope is never read |
| `queryId` on a custom action | server re-runs the source query narrowed to the selection (`CustomActionRequest.cs:45`); the client never sends it (`:368`) |
| `POST /spark/po/{type}/refresh` | `Refresh.cs:26` |
| `POST /spark/po/{type}/new`, `.../delete-row` | `New.cs:23`, `DeleteRow.cs:40` |
| `/spark/lookupref/*` (5 endpoints) | `Endpoints/LookupReferences/*` |
| `GET /spark/types/{id}`, `GET /spark/actions/{type}` | `EntityTypes/Get.cs:10`, `ListCustomActions.cs:10` |
| `GET /spark/program-units`, `/culture`, `/translations` | `ProgramUnits/Get.cs:13`, `Culture/Get.cs:9`, `Translations/Get.cs:9` |
| WebSocket `GET /spark/queries/{id}/stream` | `StreamExecuteQuery.cs:14` |

---

## Found while investigating: retry is wired to only half the hooks

⚠️ **Live defects, reachable from the browser today, not caused by anything in this proposal.**

A retry has **two** halves, and they are wired independently:

- **emit** — the endpoint catches `SparkRetryActionException` and returns `ClientResult.Retry(...)` → 449
- **accept** — the endpoint's request type binds `RetryResults[]` and feeds
  `RetryAccessor.AnsweredResults`

Measured across every endpoint:

| Endpoint | Hooks it runs | Emits 449 | Accepts answers |
|---|---|---|---|
| `POST /po/{type}` (create) | `OnSave`, `OnBeforeSave`, `OnAfterSave` | ✅ `Create.cs:103` | ✅ `:61` |
| `PUT /po/{type}/{id}` (update) | same | ✅ `Update.cs:96` | ✅ `:56` |
| `DELETE /po/{type}/{id}` | `OnDelete`, `OnBeforeDelete` | ✅ `Delete.cs:69` | ✅ `:43` |
| `POST /actions/{type}/{name}` | custom actions | ✅ `ExecuteCustomAction.cs:310` | ✅ `CustomActionRequest.cs:47` |
| `POST /po/{type}/refresh` | **`OnRefreshAsync`** | ❌ **none** | ✅ `Refresh.cs:295` |
| `POST /po/{type}/new` | **`OnNewAsync`** | ❌ **none** | ❌ **none** |
| `POST /po/{type}/delete-row` | **`OnDeleteRowAsync`** | ❌ **none** | ❌ **none** |
| `GET /po/{type}/{id}` | `OnLoadAsync` | ❌ | ❌ *structurally* — a GET has no body |
| `GET /queries/{id}/execute` | `OnQueryAsync` | ❌ | ❌ *structurally* |

Three distinct defects fall out of that table.

**D1 — `OnRefreshAsync` accepts answers it can never have asked for.** `Refresh.cs` binds
`RetryResults` and populates `AnsweredResults` (`:85-87`), but catches nothing — so a hook that calls
`Retry.Action(...)` during refresh throws `SparkRetryActionException` straight out of the endpoint. Half
a feature, wired at the answering end only.

**D2 — `OnNewAsync` / `OnDeleteRowAsync` have neither half.** A retry from either escapes as an
unhandled exception.

**D3 — `OnLoadAsync` / `OnQueryAsync` cannot participate at all.** Both endpoints are `GET`. Even if
they emitted a 449, there is no request body to carry the answer back. This is a design question, not a
missing `catch`. See **FR7c**.

> ⚠️ **Correction to this PRD's first draft.** It claimed a retry from `new`/`delete-row` produced an
> infinite loop — prompt, answer, ignored, re-prompt. That was wrong, and wrong in the optimistic
> direction: those endpoints never emit a 449 at all, because they do not catch the exception, so the
> browser never shows a prompt and the caller gets an unhandled server error instead. The infinite-loop
> reading assumed the emit half was present. **S2 must confirm the actual status code** rather than
> reason from the absence of a `catch`.

---

## Requirements

### The conversation

- **FR1** — `SparkClient` can **answer** a retry: submit `RetryResult[]` on custom action, create,
  update, delete and refresh.
- **FR2** — Answering is expressed as a **returned result with an explicit continuation**, not a
  callback invoked mid-request. See *Design → The shape of the retry API*.
- **FR3** — The loop **accumulates** answered steps: each resubmit carries all previously answered
  results, matching `spark.service.ts:375-376` and the server's
  `AnsweredResults = retryResults.ToDictionary(r => r.Step)`.
- **FR4** — A convenience overload may auto-answer with a caller-supplied policy (a delegate), built
  **on top of** FR2 rather than beneath it.
- **FR5** — The client exposes a **recursion cap** with a documented default, and exceeding it throws
  rather than spinning.
- **FR6** — Cancel semantics match the frontend: answering with an option **not** in `options` is a
  protocol error; the frontend sends `"Cancel"` and rethrows the original 449 when `Cancel` is absent
  (`spark.service.ts:373`).
- **FR7** — **A retry works from every hook that can meaningfully prompt.** Issue owner's position,
  2026-09-12: *"RetryActions should work from pretty much all action hooks + customActions."* Decided
  server-side, not worked around in the client.
  - **FR7a** — `POST /po/{type}/refresh` **emits** 449 (catch `SparkRetryActionException`). It already
    accepts answers, so this closes D1 with one `catch`.
  - **FR7b** — `POST /po/{type}/new` and `.../delete-row` **emit and accept**: add the `catch` and bind
    `RetryResults` on `NewPersistentObjectRequest` / `DeleteRowRequest`. Closes D2.
  - **FR7c** — `OnLoadAsync` / `OnQueryAsync` (D3) are **out of scope and documented as unsupported**.
    Both endpoints are `GET`; supporting them means a POST variant or an out-of-band answer channel,
    which is a protocol change, not a fix. An analyzer or a loud runtime error is preferable to silence
    — **S3** decides which.

### Client operations

- **FR8** — The envelope's `operations` are **surfaced**, not discarded, on every enveloped call.
- **FR9** — `notify` and `refreshAttribute` are **applied** to the client's own state where that is
  meaningful — `refreshAttribute` carries server-computed values that are otherwise lost
  (`operations.ts:36-44`).
- **FR10** — Unknown operation types are **ignored, not thrown**, matching the frontend's forward-compat
  contract (`dispatcher.service.ts:38-47`).
- **FR11** — Non-retry operations are surfaced **before** the retry prompt, matching `:357-358`.

### Coverage

- **FR12** — Typed methods for `refresh`, `new`, `delete-row`.
- **FR13** — Typed methods for `/spark/lookupref/*` (list, get, add, update, delete).
- **FR14** — Typed methods for `GET /spark/types/{id}`, `GET /spark/actions/{type}`,
  `/spark/program-units`, `/culture`, `/translations`.
- **FR15** — `ExecuteActionAsync` sends `queryId`.
- **FR16** — The action response envelope is read; a non-null `result` is surfaced rather than dropped.

### Fidelity

- **FR17** — For a representative multi-step flow, the client's wire traffic is **equivalent to the
  browser's** — same paths, verbs, headers and body shape. Proven by **S1**, not asserted.
- **FR18** — The client sends `X-Spark-Timezone` and `Accept-Language` when configured to. `HttpClient`
  sends neither by default and the server silently falls back
  (`RequestCultureResolver.cs:21`, `RequestTimeZoneResolver.cs`).

### Release

- **FR19** — No new NuGet package id. Everything lands in `MintPlayer.Spark.Client` or the existing
  `.Authorization` sibling.
- **FR20** — Version bumped in lockstep across all `libs/**` packages, per the repo's CI guard
  (`pull-request.yml:172-207`).

---

## Design

### The shape of the retry API — the one decision that matters

This is public API on a published package, so it is worth getting right once.

**Chosen: a returned discriminated result with an explicit continuation.**

```csharp
var result = await client.ExecuteActionAsync(typeId, "Reassign", selectedItemIds: [id]);

while (result.IsRetry)
{
    var prompt = result.Retry!;                       // Title, Message, Options, PersistentObject
    prompt.PersistentObject!.Attributes
          .Single(a => a.Name == "Reason").Value = "Customer request";

    result = await client.ContinueAsync(result, option: "Yes");   // carries prior steps forward
}
```

**Rejected: the callback-during-flight shape** that `Vidyano.Core` uses
(`Client.cs:825-847`, `Hooks.OnRetryAction`). It reads well for an app with a UI to block on, but the
answer must be produced *while the HTTP request is in flight*, so a test cannot write
"execute; inspect; answer". Vidyano's own scripting layer pays **~250 lines** — two
`TaskCompletionSource`s, an `AsyncLocal` gate, one-shot continuations, teardown draining
(`VidyanoSession.cs:35-57`, `:1867-1972`) — purely to invert that callback back into a resumable state
machine. We would pay the same tax in every test.

FR4's auto-answering overload gives the ergonomic one-liner **on top**:

```csharp
await client.ExecuteActionAsync(typeId, "Reassign",
    onRetry: prompt => RetryAnswer.Option("Yes"));
```

### What travels between hops

Following Vidyano's one genuinely good structural choice: **the conversation state is the request, not
the client.** `ContinueAsync` rebuilds the same request with `retryResults` appended. Nothing about an
in-flight action is stored on `SparkClient`, so two concurrent conversations cannot corrupt each other.

⚠️ `RetryResult.Step` is the correlation key — the server does
`AnsweredResults = retryResults.ToDictionary(r => r.Step)` and **re-runs the hook from the top** each
time, with already-answered steps returning normally. The client must preserve step numbers verbatim
and never renumber.

### The retry PersistentObject has no entity type

⚠️ The PO carried by a retry is usually a **Virtual PO with no `security.json` grant**, so
`GET /spark/types` does not contain it. The Angular modal synthesizes an `EntityType` from the PO's own
attributes (`spark-retry-action-modal.component.ts:155`, `entityTypeFromPo`).

A headless client must not try to resolve the type. It should hand back the `PersistentObject` as-is and
let the caller set attribute values directly — which is simpler than the browser's problem, not harder.
Values must be marked `isValueChanged: true` on the way back (`mergeAttributeMetadata`, `:194`).

### Client operations: surface, and apply the two that matter

| Operation | Headless behaviour |
|---|---|
| `notify` | surface — a test asserts on it |
| `refreshAttribute` | **apply** to the in-memory PO; it carries server-computed values, incl. AsDetail rows in `object`/`objects` because `value` is null for those |
| `refreshQuery` | surface — the caller decides whether to re-execute |
| `retry` | drives FR1-FR7 |
| `navigate` | surface only; meaningless headless |
| `disableAction` | surface only; **a no-op even in the browser** (`provide.ts:119-127`) |

### Not reproducing the frontend's refresh *coordination*

`RefreshCoordinator` (`po-form/src/refresh-coordinator.ts`) has real UI semantics — immediate vs
on-blur by data type, a serialized queue, and monotonic tickets where a stale response is **discarded
rather than cancelled** (`:21-25`, `:70-84`). That is a property of a form with a user typing in it.

The client exposes `RefreshAsync(...)` and nothing else. A test that wants coordination semantics is
testing the Angular form, and belongs in a browser test.

---

## Out of scope

- **External login.** Genuinely browser-dependent: `window.open` plus a `postMessage` of type
  `spark:external-login` (`SparkAuthenticationExtensions.cs:250`). A headless client uses local
  credentials. No workaround proposed.
- **Replacing the browser tests.** `ReturnUrlValidationTests`, `SmokeTests`,
  `ViewerTimezoneRenderingTests` and the CSWSH `WebSocketOriginTests` assert rendering, redirects and
  transport behaviour a request/response client cannot see. **Measured this session:** the two defects
  found in the timezone work — a `datetime-local` rendering blank, and `DatePipe` converting to the
  viewer's zone — would both have been green through a protocol client.
- **A scripting DSL.** Vidyano has `.visc`; this proposes a C# API only. A DSL is a separate decision
  with a much larger surface.
- **Migrating the 7 raw-HTTP test classes.** Enabled here, done separately — otherwise this PR carries
  both a public API change and a large test rewrite, and a failure in the rewrite is indistinguishable
  from a failure in the API.
- **WebSocket streaming client.** Reproducible with `ClientWebSocket` (`StreamExecuteQuery.cs:30`
  accepts `GET`+Upgrade and RFC 8441 `CONNECT`), but no test needs it yet and an unused API is a
  liability on a published package.

---

## Risks

### R1 — This is public API on a published package
`dotnet pack` runs solution-wide and pushes to nuget.org on merge to master
(`dotnet-build-master.yml:83`, `:149`). A shape we regret is permanent.
*Mitigation:* FR2's returned-result design is chosen specifically because the alternative is known —
from a real codebase — to force callers into a coroutine. **S1 validates the shape against real traffic
before the API is frozen.**

### R2 — The house rule says extension packages, not core additions
`SparkClient`'s own doc (`:8-29`) says new endpoint families should hang off the public `SendAsync`
*"in place of adding them to the core client"*, with `Client.Authorization` as the precedent.
*Position:* the retry loop **completes a type the core already exposes** (`SparkActionResult.IsRetry`
ships today), so it is core. The endpoint coverage in FR12-FR14 is the part that could arguably be an
extension package — but FR19 rules that out on the permanent-package-id risk, so it lands in core with
this departure stated rather than silent. **Open to reversal.**

### R3 — Widening retry to more hooks widens the blast radius
FR7 turns three endpoints that currently throw into endpoints that return 449. Any existing caller that
treats non-2xx as fatal — including `SparkClient` itself before FR1 lands — sees a new status code from
`refresh`, `new` and `delete-row`.
*Mitigation:* `SparkClient` already treats 449 as in-protocol on the action path (`:380-385`); FR1
generalises that. **The Angular client is the one to check**: `sendWithEnvelope` resubmits with no depth
limit (`spark.service.ts:375-376`), so a hook that re-raises unconditionally would spin in a browser.
FR5's cap must exist **before** FR7 ships, not after.

⚠️ This risk is the reverse of the one this PRD's first draft described. See the correction under
*Found while investigating*.

### R4 — The E2E rate-limit budget is shared
Fleet's limiter is a fixed window of **150 requests / 10s partitioned by client IP**, and every test in
the collection shares `127.0.0.1` (`SparkBuilderRateLimiterExtensions.cs:99-106`). Measured 2026-09-12:
adding two browser tests took the suite from 94/94 to 80/16, then 86/10 on a re-run — different sets
each time.
*Effect here:* **favourable.** Client-driven tests are far cheaper than browser ones. But a large
migration of tests could still concentrate traffic; the cap is per-IP, not per-test.

### R5 — Fidelity is assertable but not provable in general
S1 proves equivalence for *the flows it covers*. It cannot prove the client matches the frontend
everywhere.
*Position:* accepted, and stated rather than papered over. The value is in the flows tests actually
drive.

---

## What must be true when this is done

1. A test can execute a custom action, receive a retry prompt, set attributes on the carried
   `PersistentObject`, submit an option, and receive the next response — without touching raw
   `HttpClient`.
2. That loop works for **repeated** retries, carrying all prior answers.
3. `retryResults` on `new` / `delete-row` either works or fails loudly. Not silently forever.
4. Client operations are visible to the caller; `refreshAttribute` has been applied.
5. S1 shows the client's traffic for a full action→retry→resubmit flow is equivalent to the browser's.
6. No new package id exists on nuget.org.
7. The four browser-bound test classes are untouched and still passing.
