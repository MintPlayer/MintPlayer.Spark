# PRD: Client Operations — Unified Backend Side-Effects Envelope

## Status

**Implementation progress — partial.** Server foundations landed; endpoint wiring, SDK updates, frontend, and demo work are pending.

| Milestone | State |
|---|---|
| Server DTOs + envelope + `IClientAccessor` surface | ✅ shipped |
| `ClientAccessor` impl + `IManager.Client` wiring + retry accumulation | ✅ shipped |
| Envelope wiring through action endpoints | ⏳ pending |
| `MintPlayer.Spark.Client` SDK envelope-unwrap | ⏳ pending |
| Server unit tests (envelope contract + retry subsumption) | ⏳ pending |
| Frontend dispatcher + provider token + `SparkService` interceptor | ⏳ pending |
| Frontend handlers + notification/registry primitives | ⏳ pending |
| Fleet demo examples + Playwright E2E | ⏳ pending |

## Relationship to other PRDs

Supersedes:

- `docs/custom-actions-prd.md` §8 *"Future Phase: Navigate & Notify via IManager"* — that section's Navigate/Notify surface is a strict subset of what this PRD specifies. When this PRD ships, §8 gets a "Shipped in Client Operations PRD" banner.
- `docs/PRD-PersistentObjectFactory-Followups-II.md` §1 *"CustomAction return-value builder"* — obsolete. A builder-over-return-value was never the right shape; side-effects-accumulated-on-`IManager` generalizes further (covers CRUD actions, CustomActions, any future action type) and requires no changes to `ICustomAction.ExecuteAsync`'s signature.

## Motivation

Backend action code today has exactly one way to tell the frontend "do something after this finishes": **HTTP 449 Retry**. That single signal carries one concept — "pause and ask the user a question" — and nothing else. Everything else the framework needs to express is either missing or wedged into per-endpoint ad-hoc response shapes:

| Need | Today | Problem |
|---|---|---|
| "Show a success toast after save" | Not supported | No notification service on frontend; no API on backend |
| "After this Copy action, navigate to the new record" | Manual — frontend reads the response's `Id` and navigates | Backend can't *request* navigation; caller-side only |
| "This field changed on the server — refresh just that attribute" | Full PO re-fetch | Over-fetches the rest of the PO; loses UI state (scroll/focus/dirty-flags on other attrs) |
| "This query result is stale — refresh the list" | Component-owned signal | Not cross-component; a save on `/cars/1` can't tell an open `/cars` query to refresh |
| "Disable action `Approve` for the rest of this session" | Not supported | No dynamic-disable hook; `selectionRule` in `customActions.json` is static |
| "Ask the user a question before committing" | `manager.Retry.Action(...)` | Works, but is the only one of its kind — pattern hasn't been generalized |

Every ad-hoc addition ("let's add a `navigateTo` field to the Create response") fragments the client-handling code and paints the frontend into a corner where each endpoint needs its own response-parsing logic. A **unified envelope** where the backend accumulates typed operations and the frontend dispatches them through a single handler makes all of the above one change, not N changes.

The retry-deep-dive agent's conclusion: **subsume retry into the unified envelope**, don't run parallel pipelines. Retry becomes one operation type among many, and the pattern it pioneered (accumulate on `IManager` during action execution, serialize on response egress) extends naturally to the rest of the list above.

## Goals

1. A single **wire envelope shape** carried on every action-endpoint response (HTTP 2xx and HTTP 449), carrying the primary result plus an ordered list of **client operations**.
2. A single **backend accessor** on `IManager` (e.g. `manager.Client`) where action code accumulates operations during execution, same scoping model as `IRetryAccessor`.
3. A single **frontend dispatcher** (a service wired into the existing `SparkService` response pipeline) that iterates received operations and executes each through a registered handler.
4. Unknown operation types are **ignored by the client** (no hard failure) — enables adding operation types without breaking older client builds.
5. Works uniformly across all action types: PO CRUD endpoints (Create/Update/Delete), query execution, CustomActions, and any future action-like endpoint.
6. **Retry subsumed**: existing `manager.Retry.Action(...)` calls continue to work at the developer-API level, but internally they contribute a `retry` operation to the same envelope and travel via the same serialization path.

## Non-goals

- WebSocket / SignalR / real-time push. Operations ride on the synchronous HTTP response to the triggering action, not a persistent channel.
- Cross-session broadcasts. An operation affects *the caller's session only*. "Tell every open browser to refresh" is a different problem.
- Pre-action client state queries. The backend does not inspect or probe client state before emitting an operation.
- Replacing existing retry semantics. The user-visible behavior (HTTP 449 pauses, modal appears, user answers, request re-submitted with `retryResults[]`) stays identical. Only the internal envelope shape changes.

## Architecture

### Wire format

A single envelope wraps every action-endpoint response. The primary result (the thing the endpoint used to return directly) moves under a `result` field; operations ride alongside under `operations`.

```jsonc
// HTTP 200 — e.g. Create succeeded, backend requested navigation + toast
{
  "result": {
    "id": "cars/42",
    "name": "ABC-123",
    "objectTypeId": "...",
    "attributes": [/* ... */]
  },
  "operations": [
    { "type": "navigate", "objectTypeId": "...", "id": "cars/42" },
    { "type": "notify", "message": "Car created", "kind": "success" }
  ]
}
```

```jsonc
// HTTP 449 — retry pattern subsumed. Note: retry is just an operation.
// No primary result — action didn't complete.
{
  "result": null,
  "operations": [
    {
      "type": "retry",
      "step": 0,
      "title": "Confirm deletion",
      "message": "Type the license plate",
      "options": ["Delete", "Cancel"],
      "persistentObject": {/* ... */}
    }
  ]
}
```

```jsonc
// HTTP 200 — custom action with multiple side-effects.
{
  "result": null,
  "operations": [
    { "type": "refreshQuery", "queryId": "cars-overview" },
    {
      "type": "disableAction",
      "actionName": "Approve",
      "target": { "kind": "query", "queryId": "cars-overview" }
    },
    { "type": "notify", "message": "Approved 3 invoices", "kind": "success" }
  ]
}
```

### `disableAction` target shapes

```jsonc
// PO-scoped — disable when the named PO is open
{ "kind": "persistentObject", "objectTypeId": "...", "id": "cars/1" }

// Query-scoped — disable when the named query result is displayed
{ "kind": "query", "queryId": "cars-overview" }

// Current-response — attach to whatever the endpoint is returning
// (PO for PO endpoints, query for query-execute, etc.)
{ "kind": "currentResponse" }

// Session — rare; survives until session ends. Prefer security.json when the
// disable is permission-driven.
{ "kind": "session" }
```

**Backward compatibility note:** the wire envelope changes for *every* action endpoint's response shape. This is a breaking change. Preview-mode NuGet version bump. The frontend library bump ships in lockstep.

### Initial operation types (v1 set)

| `type` | Fields | Semantics |
|---|---|---|
| `navigate` | `objectTypeId`, `id` *or* `routeName` | Frontend navigates to the target PO / named route. |
| `notify` | `message`, `kind` (`info`/`success`/`warning`/`error`), optional `durationMs` | Frontend shows a toast. |
| `refreshAttribute` | `objectTypeId`, `id`, `attributeName`, `value` | Frontend patches the named attribute on a currently-displayed PO. If the PO isn't open, silently dropped. |
| `refreshQuery` | `queryId` | Frontend re-executes the named query if currently displayed. |
| `disableAction` | `actionName`, `target` (discriminated union — see below) | Frontend marks the named action button disabled for the given target's lifetime. |
| `retry` | `step`, `title`, `options`, `defaultOption?`, `persistentObject?`, `message?` | (Existing retry pattern, now an operation type.) Frontend opens the retry modal. HTTP status is 449 iff this operation is present. |

**Extensibility contract:** new operation types require:

1. A new DTO class in `MintPlayer.Spark.Abstractions/ClientOperations/` inheriting a common `ClientOperation` base with a discriminator `type` string.
2. A builder method on `IClientAccessor` (e.g. `manager.Client.DisableAction(...)`).
3. A frontend handler registered with the dispatcher (`ClientOperationDispatcher`).
4. No wire-format rev — unknown operation types are silently ignored by older frontends. This is **v1's single most important compatibility property**.

### Server-side API

New interface on the abstractions side, following the shape of `IRetryAccessor`:

```csharp
public interface IClientAccessor
{
    void Navigate(PersistentObject po);
    void Navigate(Guid objectTypeId, string id);
    void Navigate(string routeName);

    void Notify(string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null);

    void RefreshAttribute(PersistentObject po, string attributeName);
    void RefreshAttribute(Guid objectTypeId, string id, string attributeName, object? value);

    void RefreshQuery(string queryId);

    // Disable action — overloads by target. Vidyano has `po.DisableActions(...)` /
    // `query.DisableActions(...)` on the DTOs themselves; Spark keeps PO/Query as pure
    // DTOs (no service back-reference), so the ergonomics come from convenience overloads
    // on this accessor instead.
    void DisableActionsOn(PersistentObject po, params string[] actionNames);
    void DisableActionsOn(Guid objectTypeId, string id, params string[] actionNames);
    void DisableQueryActions(string queryId, params string[] actionNames);
    void DisableActions(params string[] actionNames);                      // target = CurrentResponse (inferred from endpoint context)
    void DisableActionsForSession(params string[] actionNames);            // rare; security.json is usually the right answer

    // Retry is exposed via the existing IRetryAccessor surface; internally it
    // accumulates a `retry` operation on this accessor when Action() is called.
    // Callers continue to use `manager.Retry.Action(...)`.
}

public enum NotificationKind { Info, Success, Warning, Error }
```

`IManager` gains a single new property:

```csharp
public interface IManager
{
    // ... existing members ...

    /// <summary>
    /// Accumulates client-side operations (navigate, notify, refresh, disable, retry)
    /// to execute on the frontend after the current action completes. Scoped per request.
    /// </summary>
    IClientAccessor Client { get; }
}
```

**Retry subsumption:** `IRetryAccessor.Action(...)` internally calls into `IClientAccessor` to push a `retry` operation, then throws `SparkRetryActionException` (same as today) to unwind. The exception handler in each endpoint no longer builds the retry-specific JSON — it reads the unified envelope off `IClientAccessor` and serializes that. This means:

- `manager.Retry.Action(...)` on the developer side: unchanged surface, unchanged semantics.
- `SparkRetryActionException`: stays, still triggers HTTP 449.
- Endpoint catch block: rewritten to emit the unified envelope.

### Scoping

`IClientAccessor` is `ServiceLifetime.Scoped` — same as `IRetryAccessor`, same as `IManager`. One instance per HTTP request. Operations accumulate in a private `List<ClientOperation>` and are drained during response serialization.

### Blocking vs non-blocking operations

Two intrinsic categories based on what the operation *does* — not a design choice:

| Category | Semantics | HTTP behavior | Examples |
|---|---|---|---|
| **Non-blocking** | Fire-and-forget. Method accumulates the operation and returns; the action keeps executing. | Rides out on whatever response eventually goes (2xx on success, 4xx/5xx on error, 449 if a blocking operation later fires). | `navigate`, `notify`, `refreshAttribute`, `refreshQuery`, `disableAction` |
| **Blocking** | Action cannot proceed without user input. Method accumulates the operation then **throws** via `SparkClientBlockException` (subclass of today's `SparkRetryActionException`, reused name for backward compat inside the framework). The framework catches, serializes the envelope, returns HTTP 449. | HTTP 449 with the full accumulated envelope (including any non-blocking operations queued before the throw). | `retry` (currently the only one) |

The canonical mental model:

```csharp
manager.Client.Notify("Processing...");      // accumulates, returns
manager.Client.RefreshQuery("cars");         // accumulates, returns
manager.Retry.Action("Confirm", [...]);      // accumulates THEN throws
// UNREACHABLE on first pass; on replay returns normally and execution continues
manager.Client.Notify("Done!");              // only reached after user answers
```

**Key property: non-blocking operations emitted before a blocking throw are preserved in the 449 response.** So the two `Notify`/`RefreshQuery` calls above are dispatched *before* the retry modal opens — which is what users want. Pre-dialog side-effects fire first, then the dialog asks for input.

**Forward-compat:** adding a new blocking operation type (hypothetical `"authorize"`, `"stepUpAuth"`, etc.) means a new method on `IClientAccessor` that accumulates + throws. The accumulator path is unchanged; the frontend dispatcher just learns a new operation type. No reshuffling of the mechanism.

### Frontend dispatcher

`@mintplayer/ng-spark` grows a new service, `SparkClientOperationDispatcher`, injected at root:

```typescript
@Injectable({ providedIn: 'root' })
export class SparkClientOperationDispatcher {
  private handlers = new Map<string, ClientOperationHandler<any>>();

  register<T extends ClientOperation>(
    type: string,
    handler: ClientOperationHandler<T>
  ): void { /* ... */ }

  dispatch(operations: ClientOperation[]): void {
    for (const operation of operations) {
      const handler = this.handlers.get(operation.type);
      if (!handler) continue; // v1 contract: unknown types silently dropped
      handler(operation);
    }
  }
}
```

`SparkService` gets a response interceptor (analog of the existing `handleRetryError`) that unwraps `{ result, operations }`, dispatches the operations, and returns the unwrapped `result` to the caller so consumer code sees the primary payload exactly as today.

Built-in handlers registered by default:
- `navigate` → `Router.navigate([...])` (or routes registered via `SparkAuthRoutePaths`-style token)
- `notify` → injects a notification service (**new — doesn't exist yet**, see Open Questions)
- `refreshAttribute` → broadcasts to a signal-based PO registry (new primitive)
- `refreshQuery` → broadcasts to a signal-based query registry (new primitive)
- `disableAction` → updates a session-scoped action-disable store (new primitive)
- `retry` → opens `SparkRetryActionModalComponent` via existing `RetryActionService.request()` — the existing modal is unchanged, only its invocation path changes

Apps can register custom handlers via `SPARK_CLIENT_OPERATION_HANDLERS` provider token for operation types beyond the v1 set.

## Implementation plan

Single PR. Preview-mode project — breaking changes are fine, no deprecation path, no rolling migration. The original three-phase breakdown (envelope+notify+navigate → refresh+disable → retry subsumption) was defensive phasing for a non-existent backward-compat constraint. Phasing buys almost nothing here: the envelope shape threads through every endpoint exactly once, the dispatcher registration pattern is shared across operation types, and retry subsumption lands cleanest when the new envelope code is fresh rather than being retrofitted weeks later.

### Scope of the PR

**Server side:**
- **New**: `MintPlayer.Spark.Abstractions/ClientOperations/` — `ClientOperation.cs` (base), `NavigateOperation.cs`, `NotifyOperation.cs`, `RefreshAttributeOperation.cs`, `RefreshQueryOperation.cs`, `DisableActionOperation.cs` + `DisableTarget` variants, `RetryOperation.cs`, `ClientOperationEnvelope.cs`, `IClientAccessor.cs`, `NotificationKind` enum.
- **New**: `MintPlayer.Spark/Services/ClientAccessor.cs` — scoped accumulator.
- **Modified**: `MintPlayer.Spark.Abstractions/IManager.cs` — add `IClientAccessor Client { get; }`.
- **Modified**: `MintPlayer.Spark/Services/Manager.cs` — inject + forward.
- **Modified**: `MintPlayer.Spark/Services/RetryAccessor.cs` — `Action(...)` now pushes a `RetryOperation` to `IClientAccessor` before throwing. Exception type stays (`SparkRetryActionException` — reused name).
- **Modified**: every action-endpoint response path (`Endpoints/PersistentObject/{Create,Update,Delete,Get,List}.cs`, `Endpoints/Queries/Execute.cs`, `Endpoints/Actions/ExecuteCustomAction.cs`, `Endpoints/PersistentObject/Edit.cs` if any) — wrap returned value in `ClientOperationEnvelope`, drain operations. Old retry-specific JSON-builders in the `catch (SparkRetryActionException ex)` blocks replaced with generic envelope serialization.
- **Modified**: error middleware (or each endpoint's error path) — 4xx/5xx responses also emit the envelope.

**Client side:**
- **New**: `node_packages/ng-spark/client-operations/src/` — operation interfaces, dispatcher service, `SPARK_CLIENT_OPERATION_HANDLERS` injection token, built-in handlers for each v1 operation type.
- **New**: `node_packages/ng-spark/services/src/spark-notification.service.ts` — toast service (MVP; may wrap `ng-bootstrap` toasts if available).
- **New**: signal-based registries for open POs / open queries / disabled actions — consumed by `refreshAttribute`, `refreshQuery`, `disableAction` handlers.
- **Modified**: `node_packages/ng-spark/services/src/spark.service.ts` — responses flow through a single interceptor that unwraps `{ result, operations }`, dispatches, returns `result` to the caller so consumer code sees the primary payload unchanged.
- **Modified**: `spark-retry-action-modal.component.ts` + `retry-action.service.ts` — the modal itself stays; invocation now comes via the dispatcher's `retry` handler, not the bespoke `handleRetryError` path.
- **Removed**: the old retry-specific 449-handling branch in `SparkService`.

**Demo / E2E:**
- **Modified**: Fleet `CarActions` — add one worked example per operation type. Ideas: `Notify("Car saved")` on successful save, `Navigate(copy)` on a future `CopyCar` CustomAction, `DisableQueryActions("cars-overview", "Approve")` after approving all items, `RefreshAttribute` on a computed field that only the server knows.
- **New**: Playwright E2E tests covering each operation type's observable behavior.
- **Modified**: existing `ConfirmDeleteCar` retry flow E2E — must pass unchanged (regression guard for retry subsumption).

### Acceptance criteria (single milestone)

- [ ] Every action endpoint returns `{ result, operations }`. Unit-level contract test: `ClientOperationEnvelopeTests.cs`.
- [ ] All five non-blocking operation types wired end-to-end with a Fleet demo call site + Playwright test per type.
- [ ] Retry subsumed — `ConfirmDeleteCar` flow works unchanged from a user's perspective. 449 response body shape follows the new envelope; old retry-specific JSON builder code is deleted.
- [ ] Frontend dispatcher silently drops unknown operation types (unit test pushes `{ type: "futureThing" }` and asserts no throw, no console error).
- [ ] Consumer-defined handlers via `SPARK_CLIENT_OPERATION_HANDLERS` work end-to-end (unit test + one worked example in a demo app).
- [ ] Existing unit/E2E test counts hold or grow. No regressions.
- [ ] Preview-version bump for the breaking wire change, in this same PR.

## Risks

- **Breaking wire change.** Every client against the Spark HTTP API breaks when this PR lands. Preview-mode project, acceptable; called out explicitly so the version bump is deliberate.
- **Retry subsumption plumbing.** The existing retry flow has subtleties — `retryResults[]` round-trips for state reconstruction, the server's replay relies on deterministic re-execution, and the retry operation is emitted *during* exception unwind rather than after a clean return. Pin it with a unit test covering "notify + retry in the same exception path" — asserts both operations in the 449 envelope, in emission order.
- **Operation ordering.** Operations execute in array order on the frontend. `Navigate(...)` before `Notify(...)` means the navigation happens first, potentially unmounting the component that would've received the toast. Emission order = execution order; document for developers.
- **New frontend primitives.** The PR introduces a notification service, an open-POs registry, an open-queries registry, and a disabled-actions store — none exist today. This is where the bulk of the frontend work lives. Consider `ng-bootstrap` toasts if available, to avoid hand-rolling.
- **Scope surface.** One PR touches every action endpoint plus significant frontend infrastructure. Review burden is real. Reviewers should focus on: (a) the envelope contract (a single test class pins the shape), (b) the retry-subsumption flow (the subtle one), (c) the dispatcher's unknown-type drop behavior (forward-compat guarantee).

## Open questions

All open questions resolved during design review. Preserved as a decision log.

1. ~~**Action-name registry for `disableAction`.**~~ **Resolved.** Action names are strings (matching `customActions.json` for CustomActions and the standard `"Create"`/`"Update"`/`"Delete"`/... set for PO actions). Targets carry their own discriminator — see "`disableAction` target shapes" in the wire-format section. Vidyano's dual `po.DisableActions(...)` / `query.DisableActions(...)` surface doesn't port: Spark keeps `PersistentObject` as a pure DTO (no service back-reference, no ambient injection). Convenience overloads on `IClientAccessor` (`DisableActionsOn(po, ...)`, `DisableQueryActions(queryId, ...)`, `DisableActions(...)`) recover the ergonomics without coupling the DTO to framework services. See `IClientAccessor` interface definition.
2. ~~**Should there be a "Confirm" operation?**~~ **Resolved: skip.** Retry handles the question-user use case. Adding a lightweight `"confirm"` operation is speculative convenience until a real friction point emerges. Slim surface area beats pre-emptive ergonomics.
3. ~~**Client-side handler registration syntax.**~~ **Resolved: `multi: true` provider token.** Matches the `SPARK_AUTH_ROUTE_PATHS` pattern already used in `@mintplayer/ng-spark-auth`, so the idiom is consistent across the family. Consumer registration:
   ```typescript
   providers: [
       {
           provide: SPARK_CLIENT_OPERATION_HANDLERS,
           useValue: { type: 'analyticsTag', handler: myAnalyticsHandler },
           multi: true,
       },
   ],
   ```
   The dispatcher injects `SPARK_CLIENT_OPERATION_HANDLERS` (typed as `ClientOperationHandlerRegistration[]`) plus its built-in map; consumer registrations win on collision.
4. ~~**Envelope on error responses.**~~ **Resolved: yes.** HTTP 400/401/403/500 responses also carry `{ result, operations }`. A backend validation error can emit `notify` to surface the error without the frontend hand-parsing error bodies. Zero extra server code — the envelope assembly doesn't short-circuit on the error path.
5. ~~**Query-execute envelope.**~~ **Resolved: yes.** Query-execute responses wrap in the same envelope (`{ result: { data, totalRecords, skip, take }, operations }`). Queries can emit operations via `CustomQueryArgs`-accessible `IManager` — not as theoretical as it first seemed. Uniform envelope beats per-endpoint carve-outs.

## Cross-references

- **Custom Actions PRD §8** (Future Phase: Navigate & Notify): superseded by this PRD. When this ships, replace §8 with a one-line pointer.
- **Followups-II §1** (CustomAction return-value builder): obsoleted by this PRD. When this ships, close Followups-II §1 with a "superseded" banner; no return-value builder will exist.
- **Retry PRD (`prd-manager-retry-action.md`)**: stays. Phase 3 touches its internals but preserves the user-facing API.
- **Manager-retry guide (`guide-manager-retry-actions.md`)**: needs a new section after Phase 3 describing `manager.Client.*`; existing retry section stays.
