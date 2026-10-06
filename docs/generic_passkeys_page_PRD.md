# PRD — The passkeys page on Spark's generic pages, and client-method retries

Status: **draft, not grilled.** Evidence is pinned to `master` @ `680794fd` and was gathered by
read-only investigation (nothing run in a browser yet — see §6 spikes).

> **Depends on [spark_composition_PRD.md](spark_composition_PRD.md)** (same PR). The library ships
> the `Passkeys`/`PasskeyRow` model and their rights as library layers, which supersedes D6 below.
> Grill so far:
> - Q1: scope is passkeys only, so the other `/account/*` pages are not converted (decided
>   2026-10-05, which resolves O4).
> - Q2/Q3 (model files per app vs library): superseded by the composition redesign.
> - Q4 (authorization): pending, folded into composition Q2.

## 1. What the investigation found

### 1.1 The page today

`/account/passkeys` (coverage.mintplayer.com, Fleet, HR) is a hand-written Angular component, not a
Spark PersistentObject or Query page.

| Fact | Evidence |
|---|---|
| The page is **library** code, mounted by every app that calls `withAccount()` (CodeCoverage, Fleet, HR; QnA excludes it; DemoApp has no `withAccount`) | `libs/node_packages/ng-spark-auth/passkeys/src/spark-passkeys.component.{ts,html}`; `ng-spark-auth/routes/src/spark-auth-routes.ts:276-277`; `apps/CodeCoverage/CodeCoverage/ClientApp/src/app/app.routes.ts:40-45` |
| Features: list (name, "Added {createdAt}", "· Synced"), inline rename (max 64), **remove without confirmation**, add (WebAuthn ceremony), empty-state alert, description paragraph, unsupported-browser alert, one error banner | `spark-passkeys.component.html`; `.ts:87-90, :104` |
| `createdAt` is printed as a raw ISO string; `isBackupEligible` and `transports` are fetched but never shown | `spark-passkeys.component.html` |
| **Mobile defect:** `mp-card` sets `overflow: hidden`, rows are `d-flex justify-content-between` without wrap, and the two non-wrapping buttons make the row wider than the card (the ISO date does wrap at its hyphen) — the overflow is clipped, so there is nothing to scroll. **Confirmed at 375 px (S4, §9)** | `@mintplayer/web-components/card/index.mjs:29`; `ng-spark/shell/src/spark-shell.component.scss:89` |
| Server: `/spark/auth/passkeys` list / `creation-options` / register / `{id}/name` / `DELETE {id}` (+ anonymous `request-options` and `sign-in`). Delete refuses the last credential (`last_credential`) | `libs/authorization/MintPlayer.Spark.Authorization/Extensions/PasskeyEndpoints.cs:67-90`; `Endpoints/Passkeys/*.cs`; `SparkCredentialInventory.WouldRemoveLastPasskey` (`DeletePasskey.cs:47-57`) |
| Passkeys are **not documents**: they are embedded as `SparkUser.Passkeys : List<SparkUserPasskey>` behind ASP.NET Identity's `IUserPasskeyStore`; uniqueness is a compare-exchange key | `Authorization.Abstractions/Identity/SparkUser.cs:89`; `Identity/UserStore.Passkeys.cs:16-27, :66` |
| Ceremony state is held by `SignInManager` in a DataProtection-protected cookie (not raw `IPasskeyHandler` state) | `Endpoints/Passkeys/PasskeyCreationOptions.cs` |
| No program unit links the page; users reach it from the hand-written `/account` overview | `apps/CodeCoverage/CodeCoverage/App_Data/programUnits.json`; `ng-spark-auth/account/src/spark-account-overview.component.ts:31` |

### 1.2 What the framework offers today

| Need | Verdict | Evidence |
|---|---|---|
| A PO with no document (one per user) | **Exists.** A JSON-only type with no `clrType`, served by `OnLoadAsync` on an Actions class resolved by name. Production precedent: `ForgeAccounts`, `Home` | `Abstractions/EntityTypeDefinition.cs:14-20`; `Services/DatabaseAccess.cs:108-109, :1129-1145`; `Services/ActionsResolver.cs:27-34`; `apps/CodeCoverage/.../App_Data/Model/ForgeAccounts.json`, `Actions/ForgeAccountsActions.cs` |
| A sub-query of non-document rows on that PO | **Exists.** `"queries": [alias]` + a `Custom.X` source returning in-memory rows of a virtual row type. Precedent: `my-accounts` over `MyAccountRow` | `spark-po-detail.component.html:238-245`; `Endpoints/Queries/Execute.cs:189-213`; `Model/MyAccountRow.json:96-108`; `Actions/MyAccountRowActions.cs` |
| "Add passkey" through New / `OnNewAsync` | **Does not fit.** `OnNewAsync` exists (`IPersistentObjectActions.cs:90`), but the grid's New only navigates to `/po/{type}/new`; the `/new` endpoint skips `OnNewAsync` for a type without `clrType`; saving a virtual type throws. And the ceremony must run in the browser, which no server hook can do | `grid/src/spark-query-grid.component.ts:594-606`; `Endpoints/PersistentObject/New.cs`; `DatabaseAccess.cs:219-220` |
| Delete / rename of non-document rows | **Partially.** Default Delete needs an etag and silently does nothing; there is no save seam for virtual types. Custom actions on a virtual row type **do** work: the selection is rebuilt by re-running the query | `spark-query-grid.component.ts:613-620`; `DatabaseAccess.cs:439-440`; `Endpoints/Actions/ExecuteCustomAction.cs:232-244, 284-305` |
| Running browser code from a server action | **Missing.** The `ClientOperation` union is closed by `[JsonDerivedType]`; `IClientAccessor` has no generic push; client handlers return `void` and are not awaited. Only **retry** is an awaited round trip: the server answers 449 with a `RetryOperation`, the client awaits the user's answer, appends it to `retryResults` and re-sends the same request | `Abstractions/ClientOperations/ClientOperation.cs:11-18`; `IClientAccessor.cs:16-56`; `ng-spark/client-operations/src/handlers.token.ts`; `services/src/spark.service.ts:489-530`; `ExecuteCustomAction.cs:152` |
| Text / description blocks | **Exists, no new mechanism.** A read-only string attribute shown on the PO, filled from translations in `OnLoadAsync` (precedent: `ForgeAccountsActions`); attribute `Description` tooltips (#348); group labels as headings. A custom attribute renderer can render it as a paragraph | `po-detail.html:83, :100`; `renderers/src/spark-attribute-renderer-registry.ts` |
| Horizontal scroll of the generic grid on a phone | **Likely.** `mp-datatable` always wraps the table in `.datatable-scroll { overflow: auto }`; not yet checked in a browser (S4) | `@mintplayer/web-components/chunks/mp-datatable-*.mjs:87-88, :1284` |
| Model JSON / grants shipped by a library | **No, by design.** `security.json` and `App_Data/Model` are app-only | `SecurityConfigurationLoader.cs:26` and its remarks |

### 1.3 Prior art

Another framework (Vidyano) has an open, string-named "execute method" client operation that any
response can carry, dispatched to functions an app registers in the browser. It is
**fire-and-forget**: the browser's result never returns to the server, so a "browser step, then
continue on the server" flow has to be hand-chained with a second request and its own
authorization. Spark's retry protocol already has the missing half — an awaited round trip back
into the same action. Only the concept informs this design; no code is taken from it.

## 2. Goals

- **G1** `/account/passkeys` renders on the generic PO page with a generic sub-query: list, add,
  rename, remove. It uses no passkey-specific Angular component.
- **G2** The page works on a phone (375 px): every row and action is reachable.
- **G3** A **generic** framework mechanism for "a server action needs one step in the browser"
  (WebAuthn now; file pickers, clipboard and geolocation are later candidates), with authorization,
  state and the follow-up kept inside one server action.
- **G4** Explanatory text uses existing mechanisms, so the framework gains no text-block feature.
- **G5** Behaviour that exists today survives: refuse to remove the last credential, the 64-char
  name limit, a message for unsupported browsers, and no banner when the user cancels.

## 3. Non-goals

- The sign-in ceremony (`request-options` / `sign-in`) on the login page stays as it is.
- The other hand-written `/account/*` pages (profile, external logins, personal data, 2FA,
  password) are not converted here. ⚠️ Owner to confirm (O4); the one-PR rule means that if they
  are in scope, they belong in this PR rather than a follow-up.
- No push-style client operations on PO load / query execute (`Get.cs` and `Execute.cs` do not
  use the envelope). Nothing here needs them.

## 4. Design

### D1 — `Passkeys`: a virtual PO, one per signed-in user
- `App_Data/Model/Passkeys.json`, hand-authored, no `clrType`, alias `passkeys`. Attributes:
  `Description` (read-only string, from the translation `auth.passkeysDescription`) and
  `queries: ["my-passkeys"]`.
- `PasskeysActions.OnLoadAsync` **ignores the id** and resolves the user from the request
  principal. The route is `/po/passkeys/me`, with `me` as a fixed object id. If the user is
  anonymous or `SparkPasskeys` is disabled, it returns 404.
- This corrects the owner's sketch: a virtual type has no `T`, so the class cannot derive from
  `DefaultPersistentObjectActions<T>`. It is a plain Actions class, the same as
  `ForgeAccountsActions`.

### D2 — `PasskeyRow`: a virtual row type and the `my-passkeys` custom query
- `Custom.MyPasskeys(CustomQueryArgs)` reads **the current user's** `SparkUser.Passkeys`, through
  `UserManager` and the passkey store. It never reads a user id from `args.Parent`, so there is no
  IDOR.
- The Actions classes live in the Authorization assembly; resolution scans every loaded assembly
  (S2). Two traps:
  - A second class with the same simple name anywhere throws (`ActionsResolver.cs:145-155`), so
    no app may declare `PasskeysActions`/`PasskeyRowActions`/`PasskeyRenameActions`.
  - The clrType-less row source must implement `ISparkOwnsRowSecurity` with a rationale
    (`QueryExecutor.cs:1186-1199`).
- Columns:
  - Name (`auth.passkeyUnnamed` when empty)
  - Created (a `DateTimeOffset`, rendered by the generic date renderer instead of a raw ISO string)
  - Synced (bool)
- The type gets Query rights only, with no Read. It therefore has no row detail page, the same as
  `MyAccountRow`.
- The default New and Delete are disabled on the query.

### D3 — Add: custom action `AddPasskey` on `Passkeys`, through a client-method retry (D7)
- **Pass 1** creates the creation options (`SignInManager.MakePasskeyCreationOptionsAsync`) and
  calls `retry.Invoke("webauthn.create", options)`. The response is 449.
- **Browser:** `ng-spark-auth` registers `webauthn.create`. It runs
  `parseCreationOptionsFromJSON`, then `navigator.credentials.create`, and returns the credential
  JSON. Cancel (`AbortError`, `NotAllowedError`) and unsupported browsers resolve as a **cancelled**
  retry.
- **Pass 2** (same request, `retryResults` attached) performs the attestation and
  `AddOrUpdatePasskeyAsync`, then:
  - `RefreshQuery("my-passkeys")`
  - `Notify`
  - no CSRF refresh: registering changes no identity claim, and the 449 already re-issues `XSRF-TOKEN` (S5, §9)
- **Why not New / `OnNewAsync`:** see §1.2. The New button navigates to a create page, and a
  virtual type can neither run `OnNewAsync` nor save.

### D4 — Rename and Remove: custom actions on `PasskeyRow`
- **Rename** asks for the new name with a `RetryOperation.PersistentObject` form (one `Name`
  attribute, max 64) of its own virtual prompt type `PasskeyRename` (not `PasskeyRow`), then saves it through `UserManager`. This replaces inline editing. Round trip proven (S3, §9).
- **Remove** asks for confirmation with retry options (new: today there is none), then deletes. A
  `last_credential` refusal becomes a `Notify` error.
- **Authorization** is the action right in `security.json`. The selected rows are rebuilt by
  re-running `Custom.MyPasskeys`, so only the user's own passkeys can be targeted.

### D5 — Text blocks: no new mechanism
- `Passkeys.Description` (D1) carries the explanatory paragraph.
- Attribute `Description` tooltips explain the columns (for example, what "Synced" means).
- The empty state is the generic query's empty state.
- **Decided (grill Q8 = B, 2026-10-06):** a generic `paragraph` attribute renderer in ng-spark's
  core renderers shows the value full-width, without a label, as body text. It is reusable by any
  app for text blocks, and it is a renderer, not a new concept.
  - **Escaped by default.** The value is HTML-escaped (an `htmlspecialchars` equivalent) and line
    breaks are kept.
  - **Opt-out** through the existing `rendererOptions` (`EntityTypeDefinition.cs:354`):
    `"rendererOptions": { "sanitize": false }` renders the value as HTML. The HTML still goes
    through Angular's built-in `[innerHTML]` sanitizer and **never** through
    `bypassSecurityTrustHtml`, so even the opt-out cannot run scripts.
  - Text-block attributes use `showedOn: PersistentObject`, so they never become grid columns.

### D6 — Where the model and the grants live (⚠️ SUPERSEDED by spark_composition_PRD.md, kept for the record)
`security.json` and `App_Data/Model` are app-only by design (§1.2), so:
- **The library ships** the code:
  - `PasskeysActions`
  - `PasskeyRowActions` with `Custom.MyPasskeys`
  - the two custom actions
  - the translations

  This depends on S2.
- **Each app that enables passkeys** (CodeCoverage, Fleet, HR) ships:
  - `Passkeys.json`
  - `PasskeyRow.json`
  - the grants for authenticated users
- To keep the copies honest, a test asserts that each app's two JSON files agree with a reference
  copy in the library (O1).

### D7 — The generic mechanism: client-method retry
Extend the existing retry, an awaited round trip, rather than adding a fire-and-forget operation.

**Server (`MintPlayer.Spark.Abstractions` / `MintPlayer.Spark`)**
- `RetryOperation` gains `ClientMethod` (string) and `Arguments` (`JsonElement?`).
- `RetryResult` gains `Value` (`JsonElement?`).
- `IRetryAccessor` gets an overload, `Invoke(string clientMethod, Func<Task<object?>> arguments)`
  (lazy; see the trap below), that throws
  the same retry signal as `Action(...)`. On the next pass `Result.Value` holds the browser's
  answer and `Result.Option` is `"Cancel"` on failure.
- The JSON schema for `RetryOperation` gains the same fields, if the schema covers it.

**Client (`@mintplayer/ng-spark`)**
- New token `SPARK_CLIENT_METHODS` and `provideSparkClientMethods(...)`. Each entry is
  `{ name, run(args): Promise<unknown> }`, runs in an injection context, and is registered
  `multi`.
- `spark.service.ts`, in the retry path: when `retryOp.clientMethod` is set, the client awaits the
  registered method instead of `retryActionService.show`.
  - An **unknown name, a rejection or a thrown error** resolves as `Cancel`. This fails closed:
    the server can only call methods the client has registered, and nothing is evaluated.
- `MAX_RETRY_DEPTH` still bounds the loop.

**Library use:** `ng-spark-auth` registers `webauthn.create` through its `withAccount()` /
`provideSparkAuth` setup.

**Multiple phases already work, so no protocol change is needed:**
- `RetryAccessor` numbers each `Action(...)` call in order (`currentStep++`).
- It keys the answers by `Step` (`Services/RetryAccessor.cs:32-33, :51-58`).
- The client re-sends the request with all answers so far, up to `MAX_RETRY_DEPTH = 16`
  (`spark.service.ts:99, :514`).
- So "ask the browser, then ask the user, then ask the browser again" is just three calls in one
  action. The owner confirmed (2026-10-05) that retry may be changed freely if more is ever needed.

⚠️ **The trap: the action re-runs from the top on every pass.** Pass 2 could therefore call
`MakePasskeyCreationOptionsAsync` again before `Invoke` hands back the answer. That would issue a
new challenge and overwrite the state cookie, so the attestation would fail.
- **Fix:** `Invoke` takes its arguments **lazily**:
  `Invoke(string clientMethod, Func<Task<object?>> arguments)`. The factory runs only when the step
  is still unanswered.
- Code that comes before a retry must be side-effect free, or idempotent. Document this next to
  the retry docs.

**Ceremony state:** the pass-1 response is a 449 that sets the DataProtection cookie. S1 proves
that the cookie survives the 449.

### D8 — Retire the bespoke management endpoints and component
- Delete `SparkPasskeysComponent`, its secondary entry `@mintplayer/ng-spark-auth/passkeys`, its
  spec, and `SparkAuthService.passkeys/renamePasskey/removePasskey/registerPasskey`.
- `withAccount()` maps `account/passkeys` as a redirect to `/po/passkeys/me`.
- Delete the endpoints `GET /passkeys`, `POST /passkeys/creation-options`, `POST /passkeys`,
  `POST /passkeys/{id}/name` and `DELETE /passkeys/{id}`. Their logic moves into D3/D4.
  `request-options` and `sign-in` stay.
- **Decided:** the libraries are still in preview, so they keep **no backward compatibility**
  (owner, 2026-10-05). There are no shims, no deprecation period and no redirects for the removed
  API. The only exception is the user-facing `account/passkeys` route, which redirects for
  bookmarks.

### D9 — Versions
- The breaking change ships without compatibility (D8). Versions take a **minor** bump:
  `@mintplayer/ng-spark*` stays `22.x`, and the NuGet packages stay `11.x`, still in preview. The
  majors stay put (platform lockstep).

## 5. Open decisions for the owner

- **O1** Where the model JSON lives (D6): one copy per app with a sync test (recommended, keeps the
  "app-only" design position), **or** a framework addition that lets a library register virtual
  types from code.
- ~~**O2** Remove the five management endpoints or keep them.~~ **Decided:** remove them, because
  the libraries are in preview and need no backward compatibility (D8).
- ~~**O3**~~ → a `paragraph` renderer, escaped by default, with an opt-out through
  `rendererOptions.sanitize = false` that still uses Angular's sanitizer (D5).
- **O4** Convert the other hand-written `/account/*` pages in this same PR, or leave them out.
- ~~**O5**~~ → **C (grill Q9, 2026-10-06): a generic client requirement, shown disabled with a
  reason.**
  - A `SPARK_CLIENT_METHODS` entry may have an optional `supported(): boolean` and a translation key
    for its "unsupported" reason.
  - An action declares `"requiresClient": "webauthn.create"` in `actions.json`. Authorization ships
    this in its actions layer.
  - The client **disables** the action, with a tooltip, when the method is unregistered or
    unsupported.
  - The check is advisory only: the server still treats a Cancel or failure from the client method
    gracefully, and reports it.

## 6. Spikes

- **S1** On a 449 retry response, `Set-Cookie` from `MakePasskeyCreationOptionsAsync` reaches the
  browser, and pass 2 attests against it.
- **S2** `ActionsResolver` (resolution by name) and `Custom.X` sources find classes declared in the
  **Authorization library** assembly, not only in the app.
- **S3** A custom action on a virtual row type can prompt with `RetryOperation.PersistentObject`
  and read the edited value back.
- **S4** Confirm the mobile defect at 375 px with `playwright_node`. Confirm the generic sub-query
  scrolls horizontally there, using `ForgeAccounts` → `my-accounts` as the baseline.
- **S5** Why the old page called `csrfRefresh()` after registering, and whether pass 2 must still
  rotate the token.

## 7. Acceptance

- At 375 px and at desktop width, `/account/passkeys` lands on the generic PO page, and the user
  can list, add (virtual authenticator), rename and remove passkeys.
- Removing the last credential is refused with a message, and a cancelled ceremony shows no error.
- Neither the query nor the actions can see or change another user's passkeys (test).
- A client-method retry with an unknown name resolves as Cancel (unit test, client side), and
  `IRetryAccessor.Invoke` round-trips a value (unit test, server side).
- The rewritten `PasskeyCeremonyTests` E2E passes on Fleet, and the full sweep is green.

## 8. Decision log

| # | Decision | Reason / evidence |
|---|---|---|
| 1 | The page is a virtual PO with a custom sub-query, not New/`OnNewAsync` | §1.2: New navigates and skips `OnNewAsync` for virtual types; the ceremony needs the browser |
| 2 | A browser step is a retry kind, not a new fire-and-forget operation | Retry is the only awaited round trip (`spark.service.ts:489-530`). The prior art's fire-and-forget design forces hand-chained second requests, each with its own authorization. Owner asked for "a better (more generic) mechanism" (2026-10-05) |
| 3 | Explanatory text stays on existing mechanisms | §1.2 text-block row; owner: "we can do all of that already by using the existing mechanisms" |
| 4 | Prior art is concepts only | Owner, 2026-10-05: no 1:1 copying of private code |
| 5 | No backward compatibility: the endpoints, the component and the entry point are deleted outright | Owner, 2026-10-05: "the libraries are still in preview, so no backward compat is needed" |
| 6 | No retry protocol change for multiple phases; `Invoke` takes lazy arguments | Steps are already numbered and keyed (`RetryAccessor.cs:32-33, :51-58`), with depth up to 16 (`spark.service.ts:99`). The rerun-from-the-top trap is in D7 |

## 9. Spike results (2026-10-06)

### S4 — the mobile defect at 375 px: PROVEN
CodeCoverage was run at a 375×800 viewport through `playwright_node`, with one passkey registered on
a CDP virtual authenticator.

**`/account/passkeys` clips.**
- `mp-card` has `overflow-x: hidden`, with scrollWidth 340 against clientWidth 325.
- The row `div.d-flex.align-items-center.justify-content-between` is 316 against 277.
- The Remove button is cut off, and nothing can scroll; the document's scrollWidth is 375.
- The ISO date wraps at its hyphen, so the clipping comes from the two non-wrapping buttons (§1.1
  corrected).

**`/po/forge-accounts/github` scrolls.**
- Only `div.datatable-scroll` overflows: 584 against 269, with `overflow-x: auto`. Setting
  `scrollLeft = 200` stuck.
- The page itself does not scroll.
- The `my-accounts` grid had no rows, so 584 px is the header width.

**Notes for re-running.**
- The Angular dev server, started by the host, timed out twice with "Nx plugin worker … did not
  receive a load message within 10 seconds". It started with `NX_PLUGIN_NO_TIMEOUTS=true
  NX_DAEMON=false` on `dotnet run`.
- Signing in locally needed `auth.LocalCredentials = Full` (temporary, reverted) and
  `Spark__Auth__AllowUnconfirmedRegistration=true`. The local user `spikes4` is still in the
  local dev `Coverage` DB.

### S2 — library Actions classes and `Custom.X` found: PROVEN (code reading)
- `ActionsResolver.FindActionsType` scans `AppDomain.CurrentDomain.GetAssemblies()`
  (`Services/ActionsResolver.cs:115-158`, used by `ResolveByEntityName` at `:93-99`).
- `Custom.X` is a method on that actions instance (`Services/QueryExecutor.cs:1168-1204`). Custom
  actions are found the same way (`Services/CustomActionResolver.cs:71-99`).
- Traps:
  - A duplicate simple name throws (`ActionsResolver.cs:145-155`).
  - A clrType-less source needs `ISparkOwnsRowSecurity` (`QueryExecutor.cs:1186-1199`).
  - Custom-action names are deduplicated by `TryAdd`: the first one wins, silently. See D2.

### S1 — `Set-Cookie` on a 449: PROVEN for the 449 half
- **Setup.** A throw-away test ran through the real pipeline (`SparkEndpointFactory`,
  `/spark/actions/execute`). A custom action did `Response.Cookies.Append` and then `retry.Action(...)`.
- **The 449.** It carried `Set-Cookie: spike-ceremony=…; path=/; secure; samesite=strict; httponly`
  and a re-issued `XSRF-TOKEN`.
- **Pass 2.** Sent with that cookie, it saw it and returned 200.
- **Why the header survives.**
  - The middleware catch (`SparkMiddleware.cs:407-429`, `when (!context.Response.HasStarted)`)
    never clears headers.
  - `ExecuteCustomAction.cs:383` keeps the retry exception out of its catch-all.
- **Inferred, not run:** a real `PerformPasskeyAttestationAsync` on pass 2. The #439 SP1 tests
  (`PasskeyCeremonyStateTests`) already show attestation against that cookie.

### S3 — a PO prompt from a custom action on a virtual row: PROVEN (empirical)
- **Setup.** A custom action on a clrType-less row type (Custom query, `selectedItemIds` plus
  `queryId`) prompted with a virtual `PersistentObject`.
- **Result.** Pass 2 read `retry.Result.PersistentObject["Name"].GetValue<string>()` as the edited
  value, and the selection was rebuilt on both passes.
- **Client.** The retry modal builds its form from the PO's own attributes
  (`spark-retry-action-modal.component.ts:95-115`).
- **Server.** `RetryPresentation` removes only explicitly denied attributes.
- **Precedent.** Fleet's `ConfirmDeleteCar` (`CarActions.cs:151-176`) and the E2E
  `RetryActionDeleteTests`.
- **Decision.** The prompt gets its own virtual type, `PasskeyRename` (D4).

### S5 — `csrfRefresh()` after registering: NOT NEEDED (code reading)
- The call was added with #439 (`61c5c427`), as "on every session change"
  (`docs/issue_439_plan.md:307`).
- Registering (`Endpoints/Passkeys/RegisterPasskey.cs`) neither signs in again nor changes claims.
  Antiforgery binds to the user-id claim, which doesn't change.
- The 449 re-issues `XSRF-TOKEN` anyway (S1). D3 drops the refresh.

## 10. As built (plan M1, M2, M2a, M2b — 2026-10-06)

Built as D5, D7 and Q9 say, verified by a clean `dotnet build` and `nx build @mintplayer/ng-spark`;
the tests and specs are written and run in M8. Where the build differs from the text above:

- **`Invoke` is `Task Invoke(string clientMethod, Func<Task<object?>> arguments)`**, awaited by the
  action: the factory is asynchronous, so the throw on an unanswered step is too
  (`IRetryAccessor.cs`, `RetryAccessor.cs`). It shares `Action`'s step counter, so mixed
  conversations number in order (test `Invoke_and_Action_share_one_step_counter…`).
- **Wire shape of the 449.** A `retry` operation with `options: []`, `title` = the method name,
  `clientMethod` and `arguments`. The answer is `{ step, option: "OK", value }`, or
  `{ step, option: "Cancel" }` for an unknown, unsupported, rejected or throwing method. "OK" is a
  convention, not a choice the server offered.
- **Arguments are serialized at `Invoke` time with the app's HTTP JSON options** (falling back to
  `JsonSerializerOptions.Web`), so their spelling matches the envelope and a persistent object
  inside them still meets the boundary net (D13a) while the request is current. A retry with no
  persistent object passes the middleware's prompt presentation untouched (`prompt is null`).
- **Client registration is a map, not a list of `{ name, run }`:**
  `provideSparkClientMethods({ 'webauthn.create': { invoke(args), supported?(), unsupportedReason? } })`,
  multi-provided; a later registration of a name wins. `SparkClientMethodRegistry` (client-operations)
  runs `invoke` and `supported` in an injection context. An unknown name logs one `console.warn`; a
  rejection is silent (a cancelled ceremony is not an error). An unsupported method answers Cancel
  **without running**.
- **A client-method Cancel is always sent to the server**, unlike a modal's Cancel without a Cancel
  option, which rethrows: the server action decides what a cancelled browser step means.
- **The .NET protocol client** carries the same fields: `RetryActionPayload.ClientMethod/Arguments`,
  `RetryAnswer.Return(value)`, `SparkCustomAction.RequiresClient`.
- **`requiresClient`** is an `actions.json` property (`ActionsCatalogueLoader.KnownProperties`,
  `ActionsFileEntry`, hence the generated schema) listed by `/spark/actions/list` only when set. The
  grid toolbar, query card, query-list page, row menu (an inert, `aria-disabled` item, since a menu item
  cannot be `[disabled]`) and the detail action bar disable it with the translated reason as `title`:
  `common.clientUnsupported` (en/fr/nl, core `translations.json`) unless the method names its own key.
- **`paragraph` is a core renderer**, in `sparkCoreRenderers`: the `SPARK_ATTRIBUTE_RENDERERS`
  default and appended after an app's own list by `provideSparkAttributeRenderers`, so no app wires it
  and an app's own `paragraph` still wins. A new registration flag, `fullWidth`, makes the detail page
  drop the label and span the row. It has no column component: as a `showedOn: PersistentObject` text
  block it never reaches a grid. A blank line starts a paragraph, a single newline is a `<br>`.

## 10b. As built (plan M3, M4, M5 — 2026-10-06)

Verified by a clean `dotnet build MintPlayer.Spark.slnx` (0 errors, no warning in a new file, no new
warning code), `nx build @mintplayer/ng-spark-auth`, `tsc --noEmit` on the ng-spark-auth and CodeCoverage
spec configs, and every app's verify gates. Tests and specs are written and run in M8. Where the composition
system decided the shape (it supersedes D6 and O1):

- **Everything ships from the Authorization library; apps state nothing.** `App_Data/Model/Passkeys.json`,
  `PasskeyRow.json` and `PasskeyRename.json` (UUIDv5 ids stamped by `npm run stamp:library-model-ids`,
  SPARK045 green); `App_Data/actions.json` (`AddPasskey` detail, `"requiresClient": "webauthn.create"`;
  `RenamePasskey` / `RemovePasskey` query, `selectionRule "=1"`); `App_Data/security.json` (five grants to
  `@authenticated`, keys `passkeys-read`, `passkeys-add`, `passkey-rows-query`, `passkey-rows-rename`,
  `passkey-rows-remove`; SPARK047 green); `translations.json` (labels, the query, the confirmation, three
  notices; en/fr/nl). The O1 sync test is obsolete: there are no copies.
- **One non-generic seam, `ISparkPasskeyAccount`** (`Identity/SparkPasskeyAccount.cs`), registered by
  `AddSparkAuthentication<TUser>` as `SparkPasskeyAccount<TUser>`. The actions classes are resolved by name, so
  they cannot carry `TUser`; the seam closes it where it is known, as `MapSparkIdentityApi<TUser>` does for
  the endpoints. Every member starts from the request principal and takes no user, which is the isolation
  argument: another user's credential id is simply not among the caller's. The logic is the endpoints'
  (`Sanitize`, `SparkCredentialInventory`, the uniform refusals); the endpoints stay until M6.
- **Classes are `internal`** (`Actions/PasskeysActions.cs`, `Actions/PasskeyRowActions.cs`,
  `CustomActions/{Add,Rename,Remove}PasskeyAction.cs`): resolution scans `GetTypes()`, and no consumer
  needs them. `PasskeyRowActions` implements `ISparkOwnsRowSecurity` with its rationale (S2).
- **Deviation: `MyPasskeys()` takes no `CustomQueryArgs`.** `CustomQueryArgs` lives in `MintPlayer.Spark`,
  which Authorization does not reference; a zero-parameter custom query is supported, avoids a new package
  dependency, and makes "never `args.Parent`" structural rather than a promise (a test pins the signature).
- **Lazy arguments and ceremony state (D7, S1).** `AddPasskey` checks availability (side-effect free), then
  `await Retry.Invoke("webauthn.create", async () => await passkeys.CreationOptionsAsync())`; the factory,
  which mints the challenge and sets the state cookie, runs only on pass 1. The options travel as the JSON
  object (a cloned `JsonElement`), not a string. Pass 2 attests `Result.Value.GetRawText()`.
- **Challenge lifetime (owner question).** Read from the decompiled `Microsoft.AspNetCore.Identity`
  11.0.0-rc.1.26425.128: `IdentityPasskeyOptions.AuthenticatorTimeout` defaults to **5 minutes**, sent to the
  browser as `timeout` (300000 ms); `SignInManager` parks the attestation state in the
  `Identity.TwoFactorUserId` cookie (`AddIdentityApiEndpoints` → `AddIdentityCookies` →
  `AddTwoFactorUserIdCookie`, `ExpireTimeSpan = 5 minutes`, a session cookie whose ticket expires), and
  **signs it out when it is read**, so a ceremony's state is single-use. Spark overrides neither. A pass 2
  after expiry, a replayed pass 2, or one without the cookie makes `PerformPasskeyAttestationAsync` throw
  `InvalidOperationException` ("No passkey attestation is underway"); the account maps exactly that to
  `SparkPasskeyOutcome.Expired`, and the action notifies `auth.passkeyExpired` ("The passkey request expired.
  Please try again."), no 500 and no framework text. A browser-side timeout rejects in the browser and
  answers Cancel (no message).
- **Outcomes of `AddPasskey`.** Cancel or no value: nothing. A value `{ "error": … }` (the client method
  resolves this for a failure other than the user's choice, e.g. `InvalidStateError`): `auth.passkeyFailed`.
  Refusal, a credential held by another account, a store failure: `auth.passkeyFailed`, saying nothing more.
  Success: `Notify(auth.passkeyAddedNotice)` and `RefreshQuery("my-passkeys")`. The new passkey is unnamed;
  rename names it (the old page's enrollment name field is gone).
- **Rename** prompts with `PasskeyRename` (one `Name`, `maxLength` 64) filled with the current name; options
  `[auth.passkeySave, "Cancel"]`. ⚠️ `"Cancel"` stays literal, untranslated: the retry modal answers a
  dismissal with `Cancel` only when it is among the options, and shows option labels as given. A framework
  limitation for every retry prompt, not fixed here. The answer is `Sanitize`d (64 characters, no control
  characters) whatever the form allowed.
- **Deviation: Remove confirms through `actions.json`'s `confirmation`** (`actions.RemovePasskey.confirmation`,
  asked by the grid before the request), not retry options as D4 said: it is the existing, fully translated
  mechanism, and a retry prompt would carry the untranslated `"Cancel"` above. `LastCredential` is a
  `Notify` error (`auth.passkeyLastCredential`); success notifies `auth.passkeyRemovedNotice` and refreshes.
- **Client.** `sparkPasskeyError` moved from `SparkAuthService` (private) to `@mintplayer/ng-spark-auth/models`,
  shared by the service and the new `sparkAuthClientMethods` (`src/lib/webauthn-client-methods.ts`), which
  `provideSparkAuth()` registers with `provideSparkClientMethods`: `supported: passkeysSupported`,
  `unsupportedReason: 'auth.passkeyUnsupported'`, `invoke` = `parseCreationOptionsFromJSON` →
  `navigator.credentials.create` → the credential's JSON; cancelled / no credential reject (Cancel), any
  other failure resolves `{ error }`. ⚠️ M7: raise ng-spark-auth's `@mintplayer/ng-spark` peer range to the
  minor that ships `provideSparkClientMethods`.
- **Routing (M5).** `withAccount()` and `withPasskeys()` map the passkeys path to `{ canActivate: [...guard,
  toPasskeysPage], children: [] }`, forwarding to `SPARK_PASSKEYS_PAGE_URL = '/po/passkeys/me'`. A guard and
  not `redirectTo`, because Angular refuses `canActivate` beside `redirectTo`, and the sign-in guard must run
  first (the router acts on the first non-passing guard in order). An app that supplies its own component
  for the entry keeps it. CodeCoverage's `/passkeys` still redirects to `account/passkeys` for that reason.
- **Gates.** CodeCoverage, Fleet, HR **and QnA** (it references the library too; its passkeys are disabled, so
  the page 404s there) were re-synchronized. `modelHashes.json`: three library-shipped types and the
  `actions.json` layer added, no app model file written. `securityPosture.txt`, identical in all four: the
  `## Layers` line `authorization | MintPlayer.Spark.Authorization | 45c4fc153e9d` and exactly five rows,
  `Signed-in users (@authenticated) | grant | {Read,AddPasskey}/Passkeys, {Query,RenamePasskey,RemovePasskey}/PasskeyRow | authorization:… | authorization`.
  Nothing in either anonymous section moved. All eight verify gates exit 0; DemoApp (no Authorization)
  was unchanged and exits 0.
- **Goldens.** `codecoverage-` and `qna-translations.golden.txt` gained the 17 new library keys and
  `common.clientUnsupported`, which M2 added to the core without updating them (both were already stale).
