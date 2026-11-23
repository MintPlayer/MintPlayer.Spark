# Notifications: one bell UI, and async notifications for anonymous visitors — PRD

Issue [#710](https://github.com/MintPlayer/MintPlayer.Spark/issues/710). Plan:
[notification_bell_plan.md](notification_bell_plan.md). Branch `feat/issue710-notification-bell`, **one pull request**.
It is released before #657 (Formatting, draft PR #709) resumes (#657 PRD D14).

## The problem

#657 starts long-running runs whose result arrives **after** the request has ended. `manager.Client.Notify` only works
during the request. `MintPlayer.Spark.Notifications` delivers stored notifications live, but only to **signed-in**
users. The apps also show them in two different UI systems: the hand-rolled `<spark-toast-container>` and
`<spark-notification-bell>`. This issue makes one notification experience for everyone, signed in or not.

## Decisions already taken (grill of #657, Q12–Q14, owner, 2026-11-23)

- **D1. One UI system: the notification bell with its dropdown.** The separate toast container goes away, so no app
  shows both.
- **D2. A short pop-out anchored to the bell** appears when something arrives.
  - Live notifications land in the dropdown and pop out.
  - In-request `Notify` messages pop out the same way. They stay in the dropdown as **unsaved session entries**: they
    are not stored and are gone after a reload.
  - `<spark-toast-container>` is deleted.
- **D3. Anonymous visitors get the same experience, asynchronous notifications included.**

## Investigation (2026-11-23, master `ff66fd0a6`)

Three read-only passes, one per spike in the issue: S1 visitor-id filtering, S2 Toast, S3 claim on sign-in. I checked
the key claims against the code afterwards. Paths are under `libs/` unless they say otherwise.

### Pass 1 (S1): row filtering and the live socket

- **Row filter.** `NotificationActions.GetRowFilterAsync` → `OwnRows.For(accessor, n => n.UserId)`
  (`notifications/MintPlayer.Spark.Notifications/Actions/NotificationActions.cs:22-23, 57-66`).
  - It builds `UserId == caller` as an expression, so the id reaches RQL as a parameter.
  - A caller with no id gets `_ => false`.
  - The queries only add `IsRead` (`:26, :29`), and there is no dedicated index.
- **The caller.** `NotificationCaller.Id` reads the `NameIdentifier` claim (`Endpoints/NotificationEndpoints.cs:19-22`).
  It is internal to the package; there is no shared "current principal" abstraction.
- **The document has one owner.** `Notification.UserId` is a single string (`Models/Notification.cs:15`). The request
  type `SparkNotification` takes `UserIds` and fans out to one document per user.
- **The live socket keeps the handshake's `HttpContext`.** `LiveConnection.RunAsync` runs its worker in the same
  execution context, so `IHttpContextAccessor` returns the socket's context in every scope
  (`spark/MintPlayer.Spark/Live/LiveConnection.cs:31-33, 88-90`).
  - So the handshake's cookies are readable in `OwnRows`, with no special case per query. This answers S1's question.
  - **Consequence:** a socket opened *before* the visitor cookie was issued never sees that cookie. The client has to
    reconnect the socket once a visitor id exists (**S1b** below).
- **Anonymous callers are refused in three places today:**
  - `App_Data/security.json` grants `QueryRead`/`Delete` on `Notification` only to the slot `notifications:users`.
  - `NotificationsGroup` has `RequireAuthorization()` (`NotificationEndpoints.cs:16`), so mark-read refuses visitors.
  - `OwnRows` returns `false` for a caller with no id.
- **The security schema already has an `@anonymous` token** (`schemas/security.schema.json:21, 75`). An application can
  bind `notifications:users` to it, so no new rights machinery is needed.
- **There is no visitor concept yet.** Data Protection is already used elsewhere (`ModerationIpHasher`,
  `WebhookSecretProtector`, `UserStore.Secrets`).
- **Expiry needs no spike.**
  - `Notification : IExpiring` defaults to `expiresAfter` 90 days.
  - `ExpirationStamper` applies the model default only when `ExpiresAtUtc` is null on a new document
    (`spark/MintPlayer.Spark.RavenDB/Expiration/ExpirationStamper.cs:55-61`), so a visitor document sets its own 24 h.
  - `ExpirationStartup` enables the database sweep.
- **Channels.** `SparkNotifier` loads a `SparkUser` per id and skips unknown ids (`Services/SparkNotifier.cs:52-58`).
  Push goes through the outbox (`:81-97`), mail through `ISparkMailer` (`:99`), and preferences are keyed by user id
  (`:133-141`). Visitors therefore need their own branch: in-app only.
- **Live socket quota.** An anonymous socket is counted per remote IP (`LiveSocket.UserKey`, `LiveSocket.cs:89-92`).
  That is unchanged.

### Pass 2 (S2): ng-bootstrap Toast in ng-spark

- **No new dependency.** `ng-spark` already peer-depends on `@mintplayer/ng-bootstrap ^22.35.0` and `@mintplayer/ui-core`.
  It imports about 30 entry points, `dropdown`, `modal` and `tooltip` among them, but not `toast` yet.
- **The Toast entry point (22.35.0):**
  - `BsToastComponent` (`bs-toast`, model `visible`, `politeness`), `BsToastHeaderComponent`, `BsToastBodyComponent`.
  - `BsToasterComponent` (`queue: ToastQueue`, `placement`). Placement is **fixed viewport corners only**
    (`toast-container position-fixed`, `ui-core/toast/index.mjs:30-41`); there is no anchoring.
- **Zoneless and SSR.** `ToastQueue` uses raw `setTimeout` (enter, autohide 5000 ms, remove) and no `PendingTasks`
  (`ui-core/toast/index.mjs:100-128`).
  - Zoneless it works, because the queue is a `Machine` and `useCore` subscribes.
  - On SSR, a push keeps the render busy for the timeout, so **never pop out on the server**. Today's toast service has
    the same bare `setTimeout` (`client-operations/src/notification.service.ts:32`).
- **Anchoring.** The bell already opens a CDK connected overlay holding `BsDropdownMenuComponent`
  (`notifications/src/spark-notification-bell.component.ts:19-79`). It deliberately avoids ng-bootstrap's dropdown
  directive (README :131-135, ng-bootstrap #419).
  - The pop-out reuses that pattern: a second connected overlay on the bell, holding plain `<bs-toast>`s.
  - It does not use `bs-toaster`, which cannot anchor. `[bsPopover]` is trigger-driven, not a programmatic stack.
- **Where `Notify` goes today.** `client-operations/src/provide.ts:48-55` calls `SparkNotificationService.show(message,
  kind, durationMs)`, and `toast-container.component.ts` renders `toasts()`.
  - The names `SparkNotificationService` (client-operations) and `SparkNotificationsService` (notifications) differ by
    one letter.
- **The apps:**
  - `<spark-toast-container />` is in `app.html` of CodeCoverage, Fleet, MintPlayer, QnA, SeoTester and SparkId (line 3).
  - The bell is only in SeoTester (`shell/shell.component.html:9`) and the template (`libs/templates/.../shell.component.ts:43`).
  - DemoApp and HR use neither (they don't use `client-operations`).
  - E2E locators use the toast container: `tests/MintPlayer.Spark.E2E.Tests/PasskeyCeremonyTests.cs:206` and
    `QnA/QnAContributionsBrowserTests.cs:156`.

### Pass 3 (S3): claiming on sign-in

- **Every fresh sign-in already passes one hook:** `ISparkSignedInHandler<TUser>.OnSignedInAsync(user, ct)`
  (`authorization/MintPlayer.Spark.Authorization/Identity/SparkAccountContracts.cs:107`). It is called from
  `SparkSignInManager.SignInWithClaimsAsync` (`Identity/SparkSignInManager.cs:126-160`), on a fresh sign-in only.
- The paths that reach it:
  - password (`SparkSignInManager.cs:110`);
  - SparkId password, two-factor and recovery code;
  - external login, auto-provisioning and the PWA popup (`ExternalLogin/ExternalLoginCallback.cs:73, :205`);
  - an external link confirmed by email;
  - external login with two-factor;
  - passkeys (`Passkeys/PasskeySignIn.cs:51`);
  - the relying apps (HR, Fleet, QnA), which reach SparkId through `AddOpenIdConnect` and land in
    `ExternalLoginCallback`.
- Refreshes are not fresh sign-ins and do not run it, which is correct here.
- **Precedent.** Organizations registers `PersonalOrganizationOnSignIn<>` with `TryAddEnumerable`
  (`organizations/.../SparkOrganizationsExtensions.cs:76`).
- **The handler can read and clear the cookie.** It injects `IHttpContextAccessor`, and the response has not started.
- **Failures:** a handler error is logged and the sign-in goes ahead (`SparkSignInManager.cs:150-157`). The claim must
  therefore be idempotent, and an unclaimed document still dies by expiry.
- **Cost:** the contract asks for a cheap handler, so the claim only queries when the cookie is present.
- **Export and deletion:** `Identity/NotificationAccountHooks.cs:28-30, 54-56` query `n.UserId == userId`. A claim that
  sets `UserId` (and clears `VisitorId`) is therefore covered with no change.
- **Coupling:** Notifications already references `MintPlayer.Spark.Authorization` directly, so no new dependency.

## Design (proposed; items marked **Q** need the owner)

### Server: `MintPlayer.Spark.Notifications`

- **R1. Visitor id.** `ISparkVisitor` (scoped) has two members:
  - `string? Current`: reads `spark.visitor`.
  - `string Ensure()`: issues the cookie when absent and returns the id.

  The cookie:
  - holds a random 128-bit id, protected with Data Protection (purpose `MintPlayer.Spark.Notifications.Visitor`);
  - is `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, with an expiry equal to the visitor expiry (R4), renewed by each
    `Ensure()`;
  - is issued **only** by `Ensure()`, which an app calls when an anonymous request starts something that will notify
    later. A plain visit never gets the cookie;
  - is strictly necessary for a service the visitor requested, so no consent banner (written in the guide).

  A signed-in caller's `Ensure()` returns null. The caller then addresses the user instead.
- **R2. Addressing.**
  - `SparkNotification` gains `VisitorIds` beside `UserIds`.
  - `Notification` gains `VisitorId` (one owner per document, as `UserId`), and exactly one of the two is set.
  - Visitor ids are stored as `visitor:<id>`, so they can never equal a `SparkUsers/…` id.
  - There is no API to enumerate or address another visitor: an app only has the id from `Ensure()` in the request
    that started the work.
- **R3. Row filter.** `OwnRows` becomes "`UserId == me` or `VisitorId == my visitor`".
  - Each side is present only when that identity exists, and both values go in as parameters.
  - Anonymous with no cookie stays `_ => false`.
  - `NotificationCaller` gains the visitor id, and the mark-read endpoint accepts a visitor caller.
  - `PushSubscription` and `NotificationPreference` stay user-only.
- **R4. Expiry.** A visitor document gets `ExpiresAtUtc = now + Spark:Notifications:VisitorExpiry` (default 24 h).
  The stamper keeps an explicit value (Pass 1).
- **R5. Channels.** Visitors get in-app only. `SparkNotifier` has a visitor branch that writes the documents and nothing
  else.
- **R6. Claim on sign-in.** `NotificationVisitorClaim<TUser> : ISparkSignedInHandler<TUser>` is registered by
  `AddNotifications()` with `TryAddEnumerable`. When the cookie is present, it:
  1. patches every `VisitorId == me` document to `UserId = user.Id, VisitorId = null`, with the expiry reset to the
     user default;
  2. deletes the cookie.

  It is idempotent, and it is a no-op without a cookie.
- **R7. Rights.** The library's `security.json` is unchanged. An application that wants visitor notifications binds the
  slot to anonymous callers too: `"notifications:users": [ "Users", "@anonymous" ]`. The guide says so, and a visitor
  without the binding gets the normal 404.
- **R8. Endpoint group.** `NotificationsGroup` drops `RequireAuthorization()`. Mark-read authorizes inside:
  - a caller with neither identity gets 401;
  - the push endpoints keep requiring a user.

  This touches `CsrfSurfaceTests` and the route snapshot.

### Client: `@mintplayer/ng-spark`

- **R9. One session store.**
  - `SparkNotificationsService` (`notifications`) holds three things:
    - the live server lists;
    - **session entries**: `Notify` messages, kept in memory only;
    - the pop-out queue.
  - `client-operations`' `Notify` handler pushes a session entry into that service.
  - `SparkNotificationService` (client-operations) and `<spark-toast-container>` are deleted (break, minor release).
- **R10. The bell works without the server package.** CodeCoverage, Fleet, MintPlayer, QnA and SparkId don't call
  `AddNotifications()`, so the bell needs a mode for that. How it detects the mode is **S4**. Without the package it
  shows session entries only:
  - no unread count from the server;
  - no push toggle;
  - no "all notifications" link.
- **R11. Pop-out.**
  - It is a second CDK connected overlay on the bell, holding `<bs-toast>`s from `@mintplayer/ng-bootstrap/toast`. It is
    `aria-live` (`politeness`), and clicking one opens the entry.
  - Live notifications pop out only when they **arrive**. The initial load does not pop out.
  - **Browser only:** nothing pops out during SSR (Pass 2).
  - **Several tabs:** a live notification pops out once. Each tab records the shown ids in `localStorage`, wrapped in
    try/catch. Session entries belong to the tab whose request produced them, so they need no deduplication.
- **R12. The socket after `Ensure()`.** A visitor's socket opened before the cookie existed must reconnect (Pass 1).
  The mechanism is **S1b**. The proposal: a response that issued the cookie carries a client operation (`visitorIssued`);
  the live client reconnects on it, and the subscriptions resume.
- **R13. Missing bell.** In dev mode, a `Notify` that arrives with no bell rendered logs a console warning.
- **R14. Apps.**
  - CodeCoverage, Fleet, MintPlayer, QnA, SeoTester and SparkId replace `<spark-toast-container />` with the bell in
    their header.
  - SeoTester already has the bell, so it just drops the container.
  - The template is already right.
  - The two E2E locators move to the pop-out.

## Open questions for the owner

- **Q1. DemoApp and HR.** The issue lists DemoApp ("renders the bell with session entries and visitor notifications").
  The code says otherwise:
  - DemoApp and HR use neither the toast container nor `client-operations`.
  - DemoApp is "core only, no Authorization, new features don't land there" (CLAUDE.md).
  - Notifications requires Authorization (it references the package, and `AddAuthentication` is in its setup).

  **Proposed:** neither app changes. Visitor notifications are proven in a test host (and SeoTester, Q2) instead.
  Decoupling Notifications from Authorization is out of scope.
- **Q2. Which production app proves the anonymous path?** SeoTester is the only app with `AddNotifications()`, and its
  scans are already started anonymously.
  **Proposed:** bind `@anonymous` in SeoTester and notify the visitor when an anonymous scan finishes. #657 (CodeCoverage)
  then adds `AddNotifications()` on its own branch. Alternative: no production app changes in this PR.
- **Q3. Cookie expiry.** **Proposed:** equal to `VisitorExpiry` (24 h), renewed by every `Ensure()`, so the cookie never
  outlives its last notification by more than that.
- **Q4. Server-package detection (S4).** **Proposed:** a provider flag, `provideSparkNotifications({ server: false })`,
  so the client never has to probe and log 404s. Alternatives: probe `GET /spark/notifications/…`, or a capabilities
  field.

## Spikes

| Id | Question | Status |
|---|---|---|
| S1 | Can the live queries' row filter key off a cookie-derived visitor id, with no per-query special case? | **Answered by reading** (Pass 1). To prove with a red test in M1. |
| S1b | How does an open socket learn that a visitor id now exists (R12)? | Open: try the client-operation reconnect; measure that the subscriptions resume and the first push arrives. |
| S2 | ng-bootstrap Toast in ng-spark: dependency, zoneless, SSR. | **Answered by reading** (Pass 2): dependency present, zoneless fine, no pop-out on SSR, anchor through a CDK overlay. To prove: `<bs-toast>` in a connected overlay in a zoneless vitest spec, and an SSR render of an app with the bell, without a hang. |
| S3 | The claim-on-sign-in hook: does it cover external logins and passkeys? | **Answered** (Pass 3): `ISparkSignedInHandler<TUser>` covers every fresh sign-in. To prove with a test for password, passkey and external login. |
| S4 | How does the bell know whether the server package is present (R10, Q4)? | Open. |

## Acceptance (from the issue)

- [ ] An anonymous visitor who starts a long-running action gets its result in the bell, after a reload too, within
      the expiry.
- [ ] A signed-in user's notifications behave as today, plus the pop-out.
- [ ] `Notify` messages pop out of the bell in every app, and `<spark-toast-container>` no longer exists.
- [ ] Signing in claims the visitor's notifications.
- [ ] Docs: `docs/guide-notifications.md`, the library README and the ng-spark docs.
- [ ] Tests:
  - row filtering by visitor id, with no cross-visitor reads (HTTP and live socket);
  - expiry;
  - claiming;
  - ng-spark specs for the bell and the pop-out;
  - `CsrfSurfaceTests` and the route snapshot (R8).
- [ ] Translation goldens are regenerated if `translations.json` changes.

## Versioning

`@mintplayer/ng-spark` loses `<spark-toast-container>` and `SparkNotificationService`. That is an API break inside
Angular 22, so it ships as a **minor**, with the break in the release notes. `MintPlayer.Spark.Notifications` gains API
inside .NET 11, also a minor. The versions come from the engine; nobody edits them by hand.
